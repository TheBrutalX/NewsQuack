using NewsQuack.App.Configuration;   // FeedConfig — the feeds this instance polls (validated non-empty URLs at boot)
using NewsQuack.App.Fetching;        // IFeedFetcher + FetchStatus/FeedFetchResult — the fetch layer under the global throttle
using NewsQuack.App.Logging;         // LogEnricher — per-worker structured-log enrichment (T-004), registered Scoped
using NewsQuack.App.Pipeline;        // PostPipelineRunner — runs every parsed post through the ordered stage chain
using NewsQuack.App.Services;        // IFeedErrorTracker — backoff + auto-remove decisions for this feed
using NewsQuack.Core;                // IFeedParser / PostContext / Post — the frozen contracts
using Microsoft.Extensions.DependencyInjection;   // IServiceScopeFactory — one scope per cycle and per feed (scoped LogEnricher)
using Microsoft.Extensions.Hosting;   // BackgroundService (plain SDK: no implicit MS.Ext usings in this project)
using Microsoft.Extensions.Logging;   // ILogger<T>

namespace NewsQuack.App.Scheduling;

/// <summary>
/// T-024: the per-instance scheduler (coordinator decision 6) — one <see cref="BackgroundService"/> per configured instance, host-managed
/// (start/stop/cancellation). Each cycle it fetches all of the instance's feeds in parallel under the single global HTTP throttle (inside the
/// fetcher), skipping auto-removed and backed-off feeds, records success/failure with the <see cref="IFeedErrorTracker"/>, parses successful
/// bodies, and runs every new post through the <see cref="PostPipelineRunner"/>. It NEVER advances feed state itself — state advances atomically
/// with delivery confirmation (T-021). Absolute slots with no drift: the first cycle runs immediately at startup, then each next slot is exactly
/// <c>last + pollInterval</c>; a slow cycle only makes the remaining time negative (clamped to zero), so we catch up without re-anchoring to "now".
/// A per-cycle exception is logged (Error) and the loop continues to the next slot — one bad cycle never kills this instance's scheduling.
/// </summary>
public sealed class InstanceScheduler : BackgroundService
{
    private readonly string _instanceId;
    private readonly TimeSpan _pollInterval;   // registration converts CheckIntervalMinutes → TimeSpan (decision 6)
    private readonly IReadOnlyList<FeedConfig> _feeds;
    private readonly IFeedFetcher _fetcher;
    private readonly IFeedErrorTracker _tracker;
    private readonly IFeedParser _parser;
    private readonly PostPipelineRunner _runner;
    private readonly IServiceScopeFactory _scopeFactory;   // scoped LogEnricher — one scope per cycle and per feed (T-004)
    private readonly ILogger<InstanceScheduler> _logger;

    public InstanceScheduler(
        string instanceId,
        TimeSpan pollInterval,
        IReadOnlyList<FeedConfig> feeds,
        IFeedFetcher fetcher,
        IFeedErrorTracker tracker,
        IFeedParser parser,
        PostPipelineRunner runner,
        IServiceScopeFactory scopeFactory,
        ILogger<InstanceScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(feeds);
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval), pollInterval, "The poll interval must be positive.");

        _instanceId = instanceId;
        _pollInterval = pollInterval;
        _feeds = feeds;
        _fetcher = fetcher;
        _tracker = tracker;
        _parser = parser;
        _runner = runner;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Absolute slots, no drift (decision 6): the FIRST cycle runs immediately at startup. `slot` is always advanced by exactly one interval —
        // it is NEVER re-anchored to "now", so a slow cycle cannot shift the whole schedule forward.
        var slot = DateTimeOffset.UtcNow;   // first slot is now ⇒ immediate first cycle

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;   // host stopped during a cycle — normal shutdown, nothing to log as an error
            }
            catch (Exception ex)   // per-cycle catch-all (impact table): one bad cycle must never kill this instance's scheduling — structured Error, continue
            {
                _logger.LogError(ex, "Feed cycle failed for instance {InstanceId}: {Message}", _instanceId, ex.Message);
            }

            slot = slot + _pollInterval;   // next ABSOLUTE slot — pure arithmetic from the previous slot (no re-anchoring to "now")

            var remaining = slot - DateTimeOffset.UtcNow;   // negative when a slow cycle already passed its slot ⇒ catch up immediately
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(remaining, stoppingToken);   // token-supervised delay in a work loop — no unsupervised wait, no drift
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;   // host stopped between cycles — normal shutdown
                }
            }
        }

        _logger.LogDebug("Scheduler stopped for instance {InstanceId}", _instanceId);   // clean cancellation reached the loop condition / a break above
    }

    /// <summary>One full cycle: activate cycle-level enrichment (instance id only), then fetch every feed in parallel — each feed task owns its own
    /// nested scope + enricher (instance id + feed url) so parallel feeds never mutate a shared enricher (decision 6, impact-table risk).</summary>
    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var cycleScope = _scopeFactory.CreateScope();
        var cycleEnricher = cycleScope.ServiceProvider.GetRequiredService<LogEnricher>();
        cycleEnricher.Set(_instanceId, null);   // instance id only — the url is unknown until a specific feed runs

        using (LogEnricher.Activate(cycleEnricher))
        {
            // All feeds in parallel under the single global throttle (inside the fetcher). A stuck feed blocks only its own task.
            await Task.WhenAll(_feeds.Select(feed => ProcessFeedAsync(feed, ct)));
        }
    }

    /// <summary>One feed for one cycle: skip auto-removed and backed-off feeds, otherwise fetch; on Ok record success + parse + run every post
    /// through the pipeline; on HttpError/TransportError/Timeout record a failure (reason from the result); RobotsBlocked is NOT a failure.</summary>
    private async Task ProcessFeedAsync(FeedConfig feed, CancellationToken ct)
    {
        // ! safe: boot validation guarantees every Feed.Url is non-null and non-empty — this guard only prevents fetching an empty/null URL.
        if (feed.Url is null or "") return;

        var url = feed.Url;

        if (_tracker.IsAutoRemoved(url))
        {
            _logger.LogDebug("Feed auto-removed — skipping without fetching: {FeedUrl}", url);   // sticky for the process lifetime (T-016)
            return;
        }

        var backoffRemaining = await _tracker.BackoffRemainingAsync(url, DateTimeOffset.UtcNow, ct);   // decision 7 — additive tracker method
        if (backoffRemaining > TimeSpan.Zero)
        {
            _logger.LogDebug("Feed in backoff — skipping: {FeedUrl}, remaining {BackoffRemaining}", url, backoffRemaining);
            return;
        }

        // Per-feed nested enrichment scope (instance id + feed url). Scoped LogEnricher ⇒ one instance per scope ⇒ parallel feeds stay isolated.
        using var feedScope = _scopeFactory.CreateScope();
        var feedEnricher = feedScope.ServiceProvider.GetRequiredService<LogEnricher>();
        feedEnricher.Set(_instanceId, url);

        using (LogEnricher.Activate(feedEnricher))
        {
            var result = await _fetcher.FetchAsync(url, ct);   // caller cancellation propagates as OCE — never mapped to a status

            switch (result.Status)
            {
                case FetchStatus.Ok:
                    await RecordSuccessAndRunPostsAsync(url, result.Content, ct);   // Content is non-null on Ok (the record contract) — guarded below
                    break;

                case FetchStatus.HttpError:
                case FetchStatus.TransportError:
                case FetchStatus.Timeout:   // a client timeout is a transient feed-health condition like TransportError ⇒ record it (interpretation — see report)
                    await _tracker.RecordAttemptAsync(url, new FeedAttemptOutcome(false, result.Error ?? "fetch failed"), DateTimeOffset.UtcNow, ct);
                    break;

                case FetchStatus.RobotsBlocked:   // site policy, NOT feed health (decision 6) — no tracker call, Debug log only
                    _logger.LogDebug("Feed blocked by robots.txt — not a failure: {FeedUrl}", url);
                    break;

                default:   // defensive — an unknown status is observable but never recorded as a failure
                    _logger.LogWarning("Feed fetch returned an unhandled status {Status}: {FeedUrl}", result.Status, url);
                    break;
            }
        }
    }

    /// <summary>On a successful fetch: record the success (resets the persisted error count), parse with per-item isolation, then run every post
    /// through the pipeline. Per-item isolation: OCE stops the cycle cleanly (shutdown); any other exception logs Error and continues with the next item.</summary>
    private async Task RecordSuccessAndRunPostsAsync(string url, string? content, CancellationToken ct)
    {
        await _tracker.RecordAttemptAsync(url, new FeedAttemptOutcome(true), DateTimeOffset.UtcNow, ct);   // Ok ⇒ success (fetch-level; decision 6)

        if (content is null)
            return;   // ! safe: the fetcher contract guarantees Content on Ok — this guard only prevents parsing an absent body

        var parseResult = await _parser.ParseAsync(content, url, ct);   // per-item error isolation happens inside the parser (T-012)

        if (parseResult.FeedError is { } feedError)
            _logger.LogWarning("Feed at {FeedUrl} fetched OK but could not be parsed: {FeedError}", url, feedError);   // never a silent empty list

        // T-047: deliver each feed's backlog OLDEST-FIRST — a stable ascending sort by PublishedAt at the scheduler's batch entry. LINQ OrderBy is
        // guaranteed stable, so items with identical timestamps keep their document order; the parser output itself stays byte-identical (document order).
        foreach (var post in parseResult.Posts.OrderBy(p => p.PublishedAt))
        {
            var ctx = new PostContext { InstanceId = _instanceId, CurrentPost = post };   // exactly what the pipeline runner consumes per item

            try
            {
                await _runner.RunAsync(ctx, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // cancellation stops the cycle cleanly — propagate to ExecuteAsync's shutdown path (decision 6)
            }
            catch (Exception ex)   // defensive isolation: the runner already isolates stage failures — this guards against anything else, per item
            {
                _logger.LogError(ex, "Pipeline run failed for an item from {FeedUrl}: instance {InstanceId}, item {ItemId}", url, _instanceId, post.ItemId);
                // continue with the next item — one broken item never kills the cycle
            }
        }
    }
}
