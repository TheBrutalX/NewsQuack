using NewsQuack.App.Logging;         // LogEnricher — per-post structured-log enrichment (T-004), registered Scoped
using NewsQuack.App.Pipeline;        // BoundedSendQueue — the queue's internal ReadAsync is the reader this worker exists to own (T-024)
using NewsQuack.Core;                // ISendQueue / INotificationTarget / IDedupEngine / Post / TargetResult — the frozen contracts
using Microsoft.Extensions.DependencyInjection;   // IServiceScopeFactory — one scope per post (scoped LogEnricher)
using Microsoft.Extensions.Hosting;   // BackgroundService (plain SDK: no implicit MS.Ext usings in this project)
using Microsoft.Extensions.Logging;   // ILogger<T>

namespace NewsQuack.App.Sending;

/// <summary>
/// T-021: the per-instance single-reader send worker — one <see cref="BackgroundService"/> per configured instance, host-managed
/// (start/stop/cancellation). Drains this instance's bounded queue and sends each post through the instance's target, retrying transient
/// failures with bounded exponential backoff + full jitter (NEVER waiting less than a target-supplied RetryAfter), making the dead-letter
/// decision after MaxAttempts (or immediately on permanent failure) by calling this instance's <see cref="DeadLetterSink"/> (T-022: persist +
/// one structured Warning log + system-webhook notification — the worker no longer logs dead letters itself), and confirming delivery to the
/// dedup engine ONLY after a Delivered outcome, which is what makes the whole scheme at-least-once. A per-post exception is logged (Error) and
/// the loop continues: one bad post never kills this instance's delivery loop. T-031: when the host stops, the worker does not drop its queue —
/// it drains whatever is still pending within a bounded window (<see cref="DefaultDrainTimeoutSeconds"/> by default, env-var configurable), so
/// shutdown is lossless within the at-least-once contract.
/// </summary>
public sealed class SendWorker : BackgroundService
{
    /// <summary>The v1.0 retry budget — one fixed constant, no config key (decision 3).</summary>
    public const int MaxAttempts = 5;

    /// <summary>The v1.0 shutdown drain window in seconds — the production default (T-031 decision 2); the env var only overrides it, it never invents a schema key.</summary>
    public const int DefaultDrainTimeoutSeconds = 30;

    private readonly string _instanceId;
    private readonly BoundedSendQueue _queue;      // internal ReadAsync — the single reader this queue exists to feed (T-024)
    private readonly INotificationTarget _target;  // THIS instance's target — the worker owns all retry logic, never the target (contract)
    private readonly IDedupEngine _dedup;          // confirm ONLY after Delivered — at-least-once hinges on this (T-014)
    private readonly IServiceScopeFactory _scopeFactory;   // scoped LogEnricher — one scope per post (T-004)
    private readonly DeadLetterSink _sink;         // T-022: persist + Warning-log + notify at the dead-letter decision points (decision 6) — THIS instance's sink
    private readonly ILogger<SendWorker> _logger;

    // Test-only injectables with production defaults (decision 1): constructor injectability for deterministic tests — NOT config keys (schema frozen).
    private readonly TimeSpan _backoffBase;        // production default: 2 s
    private readonly TimeSpan _backoffCap;         // production default: 60 s
    private readonly Func<double> _jitterSource;   // production default: Random.Shared.NextDouble — [0,1)
    private readonly TimeSpan _drainTimeout;       // production default: 30 s (T-031 decision 2) — genuinely configurable at registration via the env var (decision 4)

    public SendWorker(
        string instanceId,
        ISendQueue queue,
        INotificationTarget target,
        IDedupEngine dedup,
        IServiceScopeFactory scopeFactory,
        DeadLetterSink sink,                       // T-022: this instance's dead-letter sink — keyed singleton (decision 6/7)
        ILogger<SendWorker> logger,
        TimeSpan? backoffBase = null,      // test-only: production default 2 s — deterministic-timing injectable (decision 1)
        TimeSpan? backoffCap = null,       // test-only: production default 60 s — deterministic-timing injectable (decision 1)
        Func<double>? jitterSource = null, // test-only: production default Random.Shared.NextDouble, [0,1) (decision 1)
        TimeSpan? drainTimeout = null)     // T-031 decision 2: production default 30 s — set from the env var at registration (decision 4); tests inject small windows
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(dedup);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(logger);

        if (queue is not BoundedSendQueue bounded)
            throw new ArgumentException("The send worker reads through BoundedSendQueue's internal reader — the registry only ever produces that type.", nameof(queue));

        _instanceId = instanceId;
        _queue = bounded;
        _target = target;
        _dedup = dedup;
        _scopeFactory = scopeFactory;
        _sink = sink;
        _logger = logger;
        _backoffBase = backoffBase ?? TimeSpan.FromSeconds(2);       // production default (decision 1) — not a config key
        _backoffCap = backoffCap ?? TimeSpan.FromSeconds(60);        // production default (decision 1) — not a config key
        _jitterSource = jitterSource ?? Random.Shared.NextDouble;   // [0,1) full-jitter source (decision 1)
        _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(DefaultDrainTimeoutSeconds);   // production default 30 s (T-031 decision 2/4) — not a config key
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Post post;

            try
            {
                post = await _queue.ReadAsync(stoppingToken);   // blocks until a post is enqueued — the single reader this queue exists for (T-024)
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;   // host stopped while waiting for work — normal shutdown, nothing to log as an error
            }

            try
            {
                await ProcessPostAsync(post, stoppingToken);   // per-post enrichment scope + the retry loop (decisions 3/5)
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Decision 2: a post dequeued and then lost to cancellation mid-retry is simply dropped with a Debug log — it was never confirmed, so the next
                // scheduler cycle re-parses the feed and re-enqueues it (at-least-once via re-polling; no re-queueing mechanism in v1.0).
                _logger.LogDebug("Instance {InstanceId} stopped while processing item {ItemId} — not confirmed, the next cycle will re-deliver it", _instanceId, post.ItemId);
                break;   // shutdown owns cancellation — stop cleanly, then drain whatever is still queued (T-031 decision 2)
            }
            catch (Exception ex)   // per-post catch-all: one bad post must never kill this instance's delivery loop (impact table)
            {
                _logger.LogError(ex, "Send processing failed for item {ItemId} from feed {FeedUrl}: {Message}", post.ItemId, post.SourceUrl, ex.Message);
            }
        }

        await DrainAsync();   // T-031 decision 2: the stopping token fired — give whatever is still queued a bounded window to be delivered and confirmed

        _logger.LogDebug("Send worker stopped for instance {InstanceId}", _instanceId);   // clean cancellation reached the loop condition / a break above, then the drain finished
    }

    /// <summary>
    /// T-031 decision 2: the bounded shutdown drain. The schedulers are already stopped (registration order — decision 1), so no new posts can arrive and the queue
    /// only ever empties. deadline = now + _drainTimeout; each post is processed with a DRAIN-DEADLINE token (a CTS cancelled at the deadline, NOT the stopping
    /// token) so in-flight retry waits can complete inside the window. An OCE from that token drops the post unconfirmed (Debug log — the next start re-delivers
    /// it, at-least-once) and ends the drain; an empty queue ends it immediately. Info logs mark the start (instance id, pending count, window) and the end
    /// (processed vs dropped). Everything is awaited — no sync-over-async in the shutdown path (decision 6).
    /// </summary>
    private async Task DrainAsync()
    {
        using var drainCts = new CancellationTokenSource();
        drainCts.CancelAfter(_drainTimeout);   // deadline = now + _drainTimeout — a CTS timer on the UTC clock, no local time

        var processed = 0;
        var dropped = 0;

        _logger.LogInformation("Shutdown drain started for instance {InstanceId}: {PendingCount} post(s) pending, window {DrainTimeout}", _instanceId, _queue.PendingCount, _drainTimeout);

        while (_queue.TryRead(out var post))
        {
            try
            {
                await ProcessPostAsync(post, drainCts.Token);   // the DRAIN-DEADLINE token, not the stopping token — retry waits complete inside the window (decision 2)
                processed++;   // completed — confirmed or dead-lettered (a post dead-lettered during drain is normal operation, decision 7)
            }
            catch (OperationCanceledException) when (drainCts.IsCancellationRequested)
            {
                dropped++;   // lost to the deadline — unconfirmed, the next start re-delivers it (at-least-once)
                _logger.LogDebug("Drain deadline reached for instance {InstanceId} while processing item {ItemId} — not confirmed, the next start will re-deliver it", _instanceId, post.ItemId);
                break;   // the window is spent — end the drain (decision 2)
            }
            catch (Exception ex)   // per-post isolation: one bad post must never abort the whole drain (same policy as the normal loop)
            {
                processed++;   // failed but handled by the per-post catch-all — not dropped by the deadline
                _logger.LogError(ex, "Send processing failed during shutdown drain for item {ItemId} from feed {FeedUrl}: {Message}", post.ItemId, post.SourceUrl, ex.Message);
            }
        }

        _logger.LogInformation("Shutdown drain finished for instance {InstanceId}: {ProcessedCount} processed (confirmed or dead-lettered), {DroppedCount} dropped unconfirmed", _instanceId, processed, dropped);
    }

    /// <summary>Per-post enrichment (decision 5): create a scope, resolve the scoped LogEnricher, Set(instanceId, post.SourceUrl), activate around
    /// ProcessWithRetryAsync — same nested pattern as the scheduler's per-feed scope (T-024).</summary>
    private async Task ProcessPostAsync(Post post, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var enricher = scope.ServiceProvider.GetRequiredService<LogEnricher>();
        enricher.Set(_instanceId, post.SourceUrl);

        using (LogEnricher.Activate(enricher))
            await ProcessWithRetryAsync(post, ct);
    }

    /// <summary>The retry policy (decision 3): up to MaxAttempts sends. Delivered ⇒ confirm, then done; Retryable ⇒ wait max(RetryAfter, full-jittered
    /// backoff) and try again — the RetryAfter floor is absolute, never less; PermanentFailure ⇒ dead-letter immediately; budget exhausted ⇒ dead-letter
    /// with the last error. An OperationCanceledException on caller cancellation propagates out (shutdown owns it); any other target exception is a
    /// transient failure (Retryable), logged with the exception as argument so the stack trace survives.</summary>
    private async Task ProcessWithRetryAsync(Post post, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            TargetResult result;

            try
            {
                result = await _target.SendAsync(post, ct);   // OCE on caller cancellation propagates out — shutdown owns it (decision 3)
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // the worker's own token — propagate to ExecuteAsync's shutdown path
            }
            catch (Exception ex)   // any other target exception = a transient failure, logged with the exception as argument (decision 3)
            {
                _logger.LogWarning(ex, "Target threw on attempt {Attempt} for item {ItemId}: {Message}", attempt, post.ItemId, ex.Message);
                result = TargetResult.Retryable(null, ex.Message);
            }

            switch (result.Outcome)
            {
                case TargetOutcome.Delivered:
                    await ConfirmDeliveryAsync(post, ct);   // ONLY after a Delivered outcome — at-least-once hinges on this (decision 3)
                    return;

                case TargetOutcome.PermanentFailure:
                    await _sink.RecordAsync(post, attempt, result, ct);   // T-022: immediate dead letter — persist + Warning log + notify via the sink (no retries)
                    return;

                case TargetOutcome.Retryable:
                    if (attempt < MaxAttempts)
                    {
                        var wait = ComputeWait(result.RetryAfter, attempt);   // RetryAfter floor is absolute — never less (decision 3)
                        _logger.LogInformation("Attempt {Attempt} for item {ItemId} failed ({Error}); retrying in {Wait}", attempt, post.ItemId, result.Error ?? "unknown error", wait);

                        await Task.Delay(wait, ct);   // token-supervised; cancellation during the wait follows decision 2 (drop + re-poll)
                    }
                    else
                    {
                        await _sink.RecordAsync(post, attempt, result, ct);   // T-022: budget exhausted ⇒ dead-letter with the last error (decision 3/4)
                    }

                    break;

                default:   // defensive — TargetOutcome is a closed three-value enum; an unknown value must not loop or confirm
                    _logger.LogWarning("Target returned an unhandled outcome {Outcome} for item {ItemId}", result.Outcome, post.ItemId);
                    return;
            }
        }
    }

    /// <summary>Full jitter (decision 3): jitterSource() × min(base × 2^(attempt−1), cap). The target's RetryAfter floor is ABSOLUTE — the returned wait is never less than it.</summary>
    private TimeSpan ComputeWait(TimeSpan? retryAfter, int attempt)
    {
        var exponential = _backoffBase * Math.Pow(2, attempt - 1);   // base × 2^(attempt−1) — attempt starts at 1 ⇒ first wait ≤ base
        if (exponential > _backoffCap) exponential = _backoffCap;

        var jittered = TimeSpan.FromMilliseconds(exponential.TotalMilliseconds * _jitterSource());   // [0, min(base·2^(n−1), cap)]

        return retryAfter is { } floor && floor > jittered ? floor : jittered;   // never less than RetryAfter — the floor wins
    }

    /// <summary>Confirms delivery to the dedup engine (decision 3/6). v1.0 passes newFeedState = null: the fetcher does not expose ETag/Last-Modified and no v1.0
    /// logic consumes LastItemPublishedAtUtc, so the item is marked delivered WITHOUT touching feed state (SqliteStateStore supports exactly this). M2 will
    /// advance feed state from fetch metadata at confirmation time. A confirm failure logs Error with the at-least-once note and continues — the worker never
    /// crashes over a confirm failure: the item was sent but not confirmed, so it may be delivered again on the next cycle.</summary>
    private async Task ConfirmDeliveryAsync(Post post, CancellationToken ct)
    {
        try
        {
            await _dedup.ConfirmDeliveryAsync(_instanceId, post.SourceUrl, post.ItemId, DateTimeOffset.UtcNow, null, ct);   // newFeedState = null — decision 6 (documented above)
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutdown owns it — the item was sent but not confirmed ⇒ re-delivered on the next cycle (at-least-once)
        }
        catch (Exception ex)   // confirm failure: Error with the at-least-once note, then continue — never crash the worker over a confirm failure (decision 3)
        {
            _logger.LogError(ex, "Item was sent but not confirmed — it may be delivered again on the next cycle: instance {InstanceId}, item {ItemId}, feed {FeedUrl}: {Message}", _instanceId, post.ItemId, post.SourceUrl, ex.Message);
        }
    }
}
