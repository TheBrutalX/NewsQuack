using NewsQuack.App.Configuration;   // NewsquackConfig / InstanceConfig / FeedConfig / DiscordTargetConfig — the harness builds a one-instance config
using NewsQuack.App.Fetching;        // IFeedFetcher (the real FeedFetcher from the test host)
using NewsQuack.App.Logging;         // LogEnricher — registered Scoped, resolved per cycle/feed scope by the scheduler
using NewsQuack.App.Parsing;         // FeedParser / FeedItemMapper / FeedDateParser — the REAL two-pass parser (internal ctor via InternalsVisibleTo)
using NewsQuack.App.Pipeline;        // PostPipelineRunner + the three real v1.0 stages
using NewsQuack.App.Scheduling;      // InstanceScheduler — the unit under test
using NewsQuack.App.Services;        // IFeedErrorTracker / FeedErrorTracker — backoff + auto-remove (+ decision 7's BackoffRemainingAsync)
using NewsQuack.Core;                // Post / PostContext / IStateStore — the frozen contracts
using Microsoft.Extensions.DependencyInjection;   // ServiceCollection for the scoped LogEnricher scope factory
using WireMock.Server;              // WireMockServer (exposed for stubbing feeds + robots.txt)

namespace NewsQuack.App.Tests;

/// <summary>
/// T-024 test seam: an in-memory <see cref="IStateStore"/> that persists feed-state rows (for the tracker's RecordAttempt/BackoffRemaining) AND
/// delivered items (for the dedup stage) — the two halves of the contract the scheduler exercises. T-049: it also models the pending (in-flight) set
/// with real semantics — MarkDeliveredAsync and RecordDeadLetterAsync clear it exactly like the store's atomic commits, so a slow drain never
/// re-enqueues an in-flight item. A lock guards all three collections because one cycle fetches its feeds in parallel (Task.WhenAll). One harness per test — no cross-test state.
/// </summary>
internal sealed class FakeFeedStateStore : IStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FeedState> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<(string InstanceId, string ItemId)> _delivered = new();
    private readonly HashSet<(string InstanceId, string ItemId)> _pending = new();   // T-049: the in-flight set — one entry per marked (instance, item)

    public void Seed(FeedState state) { lock (_gate) _rows[state.FeedUrl] = state; }

    /// <summary>T-047 test seam: seeds the delivered set directly (simulates a confirm from an earlier cycle without going through MarkDeliveredAsync).</summary>
    public void SeedDelivered(string instanceId, string itemId) { lock (_gate) _delivered.Add((instanceId, itemId)); }

    /// <summary>T-049 test seam: seeds the pending set directly (simulates a marker write whose enqueue never happened — the crash between mark and enqueue).</summary>
    public void SeedPending(string instanceId, string itemId) { lock (_gate) _pending.Add((instanceId, itemId)); }

    /// <summary>T-049 test seam: true when the item is currently marked pending for that instance.</summary>
    public bool IsPending(string instanceId, string itemId) { lock (_gate) return _pending.Contains((instanceId, itemId)); }

    public FeedState? GetRow(string feedUrl) { lock (_gate) return _rows.TryGetValue(feedUrl, out var row) ? row : null; }

    public Task<FeedState?> GetFeedStateAsync(string feedUrl, CancellationToken ct = default) => Task.FromResult(GetRow(feedUrl));

    public Task UpsertFeedStateAsync(FeedState state, CancellationToken ct = default)
    {
        lock (_gate) _rows[state.FeedUrl] = state;   // FeedState is an immutable record — the stored reference IS the upserted value
        return Task.CompletedTask;
    }

    public Task<bool> IsDeliveredAsync(string instanceId, string itemId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_delivered.Contains((instanceId, itemId)));
    }

    public Task MarkDeliveredAsync(string instanceId, string feedUrl, string itemId, DateTimeOffset deliveredAtUtc, FeedState? newFeedState, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _delivered.Add((instanceId, itemId));   // the real store marks delivered atomically with the state advance — same observable effect here...
            _pending.Remove((instanceId, itemId));  // ...and clears the pending marker in the SAME commit (T-049)
        }

        return Task.CompletedTask;
    }

    public Task<int> CleanupOlderThanAsync(DateTimeOffset cutoffUtc, int batchSize = 1000, CancellationToken ct = default) => Task.FromResult(0);

    public Task RecordDeadLetterAsync(DeadLetterData deadLetter, CancellationToken ct = default)
    {
        lock (_gate) _pending.Remove((deadLetter.InstanceId, deadLetter.ItemId));   // T-049: the real store clears the pending marker in the same SaveChanges as the dead-letter upsert (the scheduler never touches dead letters itself)
        return Task.CompletedTask;
    }

    public Task MarkPendingAsync(string instanceId, string itemId, CancellationToken ct = default)
    {
        lock (_gate) _pending.Add((instanceId, itemId));   // T-049: upsert semantics — a re-mark is the normal case and simply succeeds (the set models the unique row)
        return Task.CompletedTask;
    }

    public Task ClearPendingAsync(string instanceId, string itemId, CancellationToken ct = default)
    {
        lock (_gate) _pending.Remove((instanceId, itemId));   // no-op when absent — same as the real store
        return Task.CompletedTask;
    }

    public Task<bool> IsPendingAsync(string instanceId, string itemId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_pending.Contains((instanceId, itemId)));
    }

    public Task<int> CleanupAllPendingAsync(CancellationToken ct = default)
    {
        lock (_gate)   // T-049: startup cleanup — every pending entry belonged to a dead process; returns the removed count like the real store
        {
            var count = _pending.Count;
            _pending.Clear();
            return Task.FromResult(count);
        }
    }
}

/// <summary>
/// T-024 test harness: assembles the REAL production pieces — a WireMock-backed FeedFetcher (via FeedFetcherTestHost), the two-pass FeedParser,
/// the FeedErrorTracker over a recording store, and the PostPipelineRunner wired with the three real v1.0 stages (DedupCheck + ImageResolution +
/// Send) over a per-instance BoundedSendQueue — plus a scoped-LogEnricher scope factory. This lets the scheduler tests exercise the full
/// fetch→parse→pipeline path end to end without SQLite or a live host. One harness per test; <see cref="Logs"/> is shared by every component so
/// "no Error logs" assertions cover the whole cycle.
/// </summary>
internal sealed class SchedulerTestHarness : IDisposable
{
    private const string DiscordWebhook = "https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWx";

    private readonly FeedFetcherTestHost _http;
    private readonly IServiceProvider _scopeProvider;
    private readonly NewsquackConfig _config;   // T-031: kept so BuildRestartedSchedulerAsync can rebuild a FRESH send-queue registry over the same instances
    private readonly string _instanceId;
    private readonly TimeSpan _pollInterval;
    private readonly IReadOnlyList<FeedConfig> _feeds;
    private readonly IFeedParser _parser;
    private readonly PostPipelineRunner _runner;
    private readonly IServiceScopeFactory _scopeFactory;

    public WireMockServer Server => _http.Server;

    public string BaseUrl => _http.BaseUrl;   // ! safe: a started server always has its Url set (WireMock contract — same as FeedFetcherTestHost)

    public FakeFeedStateStore Store { get; }
    public FeedErrorTracker Tracker { get; }
    public SendQueueRegistry Registry { get; }

    /// <summary>Shared by the scheduler AND every pipeline piece — so "no Error logs" assertions cover the whole cycle, not just one component.</summary>
    public LogCapture Logs { get; }

    private SchedulerTestHarness(
        FeedFetcherTestHost http, IServiceProvider scopeProvider, NewsquackConfig config, string instanceId, TimeSpan pollInterval, IReadOnlyList<FeedConfig> feeds,
        FakeFeedStateStore store, FeedErrorTracker tracker, SendQueueRegistry registry, PostPipelineRunner runner, IFeedParser parser, IServiceScopeFactory scopeFactory, LogCapture logs)
    {
        _http = http;
        _scopeProvider = scopeProvider;
        _config = config;   // T-031: BuildRestartedSchedulerAsync rebuilds a fresh SendQueueRegistry from it
        _instanceId = instanceId;
        _pollInterval = pollInterval;
        _feeds = feeds;
        Store = store;
        Tracker = tracker;
        Registry = registry;
        Logs = logs;   // the SAME capture every component (scheduler + stages + runner + tracker) writes into — built in Create
        _parser = parser;
        _runner = runner;
        _scopeFactory = scopeFactory;
    }

    /// <summary>Builds a harness for one instance polling the given feed PATHS (prefixed with the WireMock base URL). The scheduler gets
    /// <paramref name="pollInterval"/> directly — tests use small values, production uses minutes (decision 6).</summary>
    public static SchedulerTestHarness Create(string instanceId, TimeSpan pollInterval, IReadOnlyList<string> feedPaths, int autoRemoveThreshold = 10, string? fallbackImageUrl = null)
    {
        var http = FeedFetcherTestHost.Create();   // real production HTTP wiring + a FeedFetcher on a dynamic port

        var feeds = feedPaths.Select(path => new FeedConfig { Url = $"{http.BaseUrl}{path}" }).ToList();   // absolute URLs into the stub server

        var config = new NewsquackConfig
        {
            Instances = [new InstanceConfig
            {
                Id = instanceId,
                CheckIntervalMinutes = 15,   // unused by the harness's scheduler (it gets pollInterval directly) — present so the config is valid-shaped
                Feeds = feeds,
                Discord = new DiscordTargetConfig { WebhookUrl = DiscordWebhook, FallbackImageUrl = fallbackImageUrl },
            }],
        };

        var store = new FakeFeedStateStore();
        var logs = new LogCapture();   // shared by the scheduler + every pipeline piece (one capture for "no Error logs" assertions)

        // Scoped LogEnricher (T-004): the scheduler creates one scope per cycle and one per feed — this minimal container supplies it.
        var scopeServices = new ServiceCollection();
        scopeServices.AddScoped<LogEnricher>();
        var scopeProvider = scopeServices.BuildServiceProvider();
        var scopeFactory = scopeProvider.GetRequiredService<IServiceScopeFactory>();

        // The REAL v1.0 stage chain (decisions 2/3/4) over the shared store + config + one per-instance queue.
        var registry = new SendQueueRegistry(config);
        var dedup = new DedupCheckStage(new DedupEngine(store, logs.For<DedupEngine>()), logs.For<DedupCheckStage>());
        var image = new ImageResolutionStage(config, logs.For<ImageResolutionStage>());
        var send = new SendStage(registry, store, logs.For<SendStage>());   // T-049: the marker write goes through the same shared store (dedup skips delivered OR pending)
        var runner = new PostPipelineRunner(new IPostStage[] { dedup, image, send }, logs.For<PostPipelineRunner>());

        var tracker = new FeedErrorTracker(store, logs.For<FeedErrorTracker>(), enableAutoRemove: true, autoRemoveThreshold);
        var parser = new FeedParser(logs.For<FeedParser>(), new FeedItemMapper(new FeedDateParser(logs.For<FeedDateParser>()), null));   // generic strategy for non-platform URLs

        return new SchedulerTestHarness(http, scopeProvider, config, instanceId, pollInterval, feeds, store, tracker, registry, runner, parser, scopeFactory, logs);
    }

    /// <summary>The absolute URL of a feed path (for reading the matching store row).</summary>
    public string FeedUrl(string path) => $"{BaseUrl}{path}";

    /// <summary>How many requests reached the given absolute path — how these tests count "exactly N fetches" (robots.txt is a different path).</summary>
    public int RequestCount(string path) => _http.IndicesOf(path).Length;

    /// <summary>A fully-wired scheduler for this harness's instance, logging into <see cref="Logs"/>.</summary>
    public InstanceScheduler BuildScheduler() => new(
        _instanceId,
        _pollInterval,
        _feeds,
        _http.Fetcher,   // the real FeedFetcher under the global throttle (T-010)
        Tracker,         // backoff + auto-remove (+ decision 7's BackoffRemainingAsync)
        _parser,         // the real two-pass parser (T-012/T-013)
        _runner,         // the real v1.0 stage chain built in Create
        _scopeFactory,   // scoped LogEnricher — one scope per cycle and per feed (T-004)
        Logs.For<InstanceScheduler>());

    /// <summary>
    /// T-031 decision 5: simulates a process RESTART over the SAME store/tracker/parser/logs but with a FRESH per-instance send queue, a fresh stage chain
    /// wired to that queue, and a fresh scheduler. The re-enqueue mechanism is NOT invented here — it is T-024's immediate first cycle (re-parses every
    /// feed at startup) plus T-014's persistent dedup (every never-confirmed item passes the check again). T-049: like a real boot, the restart runs the
    /// startup cleanup FIRST (Program.MigrateStoreAsync wipes every pending row before any worker starts — they all belong to the dead process), so an
    /// in-flight marker from the old run never parks its item. Returns the restarted scheduler and its fresh registry so the test can read the new queue.
    /// </summary>
    public async Task<(InstanceScheduler Scheduler, SendQueueRegistry Registry)> BuildRestartedSchedulerAsync()
    {
        await Store.CleanupAllPendingAsync();   // T-049: the boot-time cleanup (Program.MigrateStoreAsync) — every pending row belonged to the dead process

        var registry = new SendQueueRegistry(_config);   // FRESH queues — the old process's queue is gone with it

        // The same real v1.0 stage chain as Create, but pointing at the fresh registry (and the SAME store — persistence is what survives a restart).
        var dedup = new DedupCheckStage(new DedupEngine(Store, Logs.For<DedupEngine>()), Logs.For<DedupCheckStage>());
        var image = new ImageResolutionStage(_config, Logs.For<ImageResolutionStage>());
        var send = new SendStage(registry, Store, Logs.For<SendStage>());   // T-049: the SAME store survives the restart — that is what makes pending rows meaningful across it
        var runner = new PostPipelineRunner([dedup, image, send], Logs.For<PostPipelineRunner>());

        return (new InstanceScheduler(
            _instanceId,
            _pollInterval,
            _feeds,
            _http.Fetcher,   // the same fetch layer — a real restart would build a fresh one; only process-local caches differ, which this test does not exercise
            Tracker,         // tracker state lives in the store rows — shared across the "restart" exactly like a new process over the same DB
            _parser,
            runner,
            _scopeFactory,
            Logs.For<InstanceScheduler>()), registry);
    }

    public void Dispose()
    {
        _http.Dispose();
        if (_scopeProvider is IDisposable disposable)   // BuildServiceProvider returns a ServiceProvider in practice — dispose when possible (same pattern as FeedFetcherTestHost)
            disposable.Dispose();
    }
}
