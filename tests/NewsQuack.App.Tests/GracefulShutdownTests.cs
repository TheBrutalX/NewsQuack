using System.Diagnostics;   // Stopwatch — drain-window timing assertions (not a date — no UTC rule involved)
using NewsQuack.App.Configuration;   // NewsquackConfig / InstanceConfig / FeedConfig / DiscordTargetConfig — the registration is built from config
using NewsQuack.App.Fetching;        // AddFeedFetching — the scheduler resolves IFeedFetcher at construction (T-010)
using NewsQuack.App.Http;            // AddNewsquackHttpClients + NamedHttpClientRegistration — the named clients every piece below resolves (T-005)
using NewsQuack.App.Logging;         // LogEnricher — registered Scoped, resolved per post scope by the worker (decision 5)
using NewsQuack.App.Parsing;         // AddFeedParsing — the scheduler resolves IFeedParser at construction (T-012)
using NewsQuack.App.Pipeline;        // BoundedSendQueue / DedupEngine / SendQueueRegistry — the real pieces the worker consumes
using NewsQuack.App.Scheduling;      // InstanceScheduler + AddInstanceScheduling — the registration-order test mirrors Program.cs's exact call sequence
using NewsQuack.App.Services;        // AddFeedErrorTracking — the scheduler resolves IFeedErrorTracker at construction (T-016)
using NewsQuack.App.Sending;         // SendWorker / AddSendWorkers / DeadLetterSink / DeadLetterNotifier — the units under test
using NewsQuack.Core;                // Post / INotificationTarget / IDedupEngine / TargetResult — the frozen contracts
using NewsQuack.Infrastructure.Persistence;   // AppDbContext + SqliteConnectionInitInterceptor — exactly AddStateStore's wiring for the checkpoint test
using NewsQuack.Infrastructure.Persistence.Entities;   // DeliveredItem — the rows written to make the WAL non-empty
using Microsoft.Data.Sqlite;        // SqliteConnection — temp-file SQLite pattern (PersistenceRoundTripTests) + a long-lived keeper connection
using Microsoft.EntityFrameworkCore;   // AddDbContextFactory / UseSqlite / AddInterceptors — one context per operation, like production
using Microsoft.Extensions.DependencyInjection;   // ServiceCollection for the scoped LogEnricher scope factory (same harness as SendWorkerTests)
using Microsoft.Extensions.Hosting;   // IHostedService — asserting worker-vs-scheduler registration order
using Microsoft.Extensions.Logging;   // LogLevel — log assertions
using WireMock.RequestBuilders;       // Request.Create() for stubbing the feed (restart test)
using WireMock.ResponseBuilders;      // Response.Create() for the stub responses (restart test)
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-031: graceful shutdown — stop order pinned by registration order (decision 1), the send worker's bounded drain mode with a DRAIN-DEADLINE token (decision 2),
/// the WAL checkpoint on shutdown (decision 3), the NEWSQUACK_SHUTDOWN_DRAIN_TIMEOUT_SECONDS parsing (decision 4), and restart re-enqueue of unconfirmed posts
/// PINNED, not rebuilt (decision 5 — the mechanism is T-024's immediate first cycle + T-014's persistent dedup). Reuses the SendWorker harness patterns:
/// scriptable fake target, real BoundedSendQueue, recording store + real sink; SchedulerTestHarness for the restart test; temp-file SQLite for the checkpoint.
/// </summary>
public class GracefulShutdownTests
{
    private const string InstanceId = "inst-1";
    private const string FeedUrl = "https://example.com/feed.xml";

    /// <summary>A real RSS with two items (same shape as InstanceSchedulerTests) — each must flow through dedup → image → send into the instance's queue.</summary>
    private const string RssTwoItems = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss version=\"2.0\"><channel><title>Two posts</title>" +
        "<item><title>First post</title><link>https://example.com/first</link><guid isPermaLink=\"true\">https://example.com/first</guid>" +
        "<pubDate>Tue, 21 Oct 2025 16:42:10 -0700</pubDate></item>" +
        "<item><title>Second post</title><link>https://example.com/second</link><guid isPermaLink=\"true\">https://example.com/second</guid>" +
        "<pubDate>Tue, 21 Oct 2025 16:43:10 -0700</pubDate></item></channel></rss>";

    private static Post Post(string itemId) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ItemId = itemId,
        Title = "A title",
        PublishedAt = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero),   // always UTC
        SourceUrl = FeedUrl,
    };

    /// <summary>Assembles the worker around the REAL production pieces (same harness as SendWorkerTests) plus T-031's drainTimeout injectable.</summary>
    private static WorkerHarness CreateWorker(
        INotificationTarget target, BoundedSendQueue queue, TimeSpan? backoffBase = null, TimeSpan? backoffCap = null, Func<double>? jitterSource = null, TimeSpan? drainTimeout = null)
    {
        var capture = new LogCapture();
        var store = new RecordingStateStore();

        // Scoped LogEnricher (T-004): the worker creates one scope per post — this minimal container supplies it (same pattern as SendWorkerTests).
        var services = new ServiceCollection();
        services.AddScoped<LogEnricher>();
        services.AddHttpClient(NamedHttpClientRegistration.DiscordClientName);   // T-022: the sink's (disabled) notifier resolves through this factory — exactly what production registers (T-005)
        var provider = services.BuildServiceProvider();

        var dedup = new DedupEngine(store, capture.For<DedupEngine>());   // real engine over the recording store — confirm arguments are assertable

        // T-022: the REAL sink over the SAME recording store + a disabled notifier (null URL) — dead letters persist and log through exactly the production path.
        var sink = new DeadLetterSink(InstanceId, store,
            new DeadLetterNotifier(null, provider.GetRequiredService<IHttpClientFactory>(), capture.For<DeadLetterNotifier>()),
            capture.For<DeadLetterSink>());

        var worker = new SendWorker(
            InstanceId, queue, target, dedup, provider.GetRequiredService<IServiceScopeFactory>(), sink, capture.For<SendWorker>(),
            backoffBase: backoffBase, backoffCap: backoffCap, jitterSource: jitterSource, drainTimeout: drainTimeout);   // T-031 decision 2/4: the drain window is injectable like the other timing knobs

        return new WorkerHarness(worker, store, capture, provider);
    }

    private sealed class WorkerHarness : IDisposable
    {
        private readonly ServiceProvider _provider;

        public WorkerHarness(SendWorker worker, RecordingStateStore store, LogCapture capture, ServiceProvider provider)
        {
            Worker = worker;   // every test stops the worker before it ends — Dispose then only releases the scope factory (same pattern as SendWorkerTests)
            Store = store;
            Capture = capture;
            _provider = provider;
        }

        public SendWorker Worker { get; }
        public RecordingStateStore Store { get; }
        public LogCapture Capture { get; }

        public void Dispose() => _provider.Dispose();   // the worker is already stopped by the test — disposing its scope factory is safe then
    }

    /// <summary>Per-item scriptable INotificationTarget fake: each configured item id gets its own per-attempt function (attempt 0, 1, …) — so a decoy post can be
    /// "always Retryable" while the subject post is "fail once, then deliver", independent of how many times the other was sent. A lock guards the counters because
    /// the worker runs on its own task.</summary>
    private sealed class PerItemScriptedTarget : INotificationTarget
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Func<int, TargetResult>> _scripts;   // itemId → per-attempt function (the LAST attempt's value repeats)
        private readonly Dictionary<string, int> _attempts = new();

        public PerItemScriptedTarget(params (string ItemId, Func<int, TargetResult> Script)[] scripts)
            => _scripts = scripts.ToDictionary(s => s.ItemId, s => s.Script);

        /// <summary>Sends for one item id — pins "exactly N attempts for THIS post" while the worker may already be on another one.</summary>
        public int SentCountFor(string itemId) { lock (_gate) return _attempts.GetValueOrDefault(itemId); }

        public Task<TargetResult> SendAsync(Post post, CancellationToken ct = default)
        {
            Func<int, TargetResult> script;
            var attempt = 0;   // 0-based attempt number for this item — read and bumped under the lock below
            lock (_gate)
            {
                script = _scripts[post.ItemId];   // the test only ever enqueues scripted item ids — an unknown one is a programming error, surfaced loudly
                attempt = _attempts.GetValueOrDefault(post.ItemId);
                _attempts[post.ItemId] = attempt + 1;
            }

            return Task.FromResult(script(attempt));   // immediate result — no suspension point between dequeue and the worker's retry wait (the decoy design relies on it)
        }
    }

    /// <summary>Polls a condition every 5 ms until it holds or the (generous) timeout passes — worker work happens on its own task.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 1000 && !condition(); i++)   // up to ~5 s — far beyond every pinned wait in these tests
            await Task.Delay(5);

        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    /// <summary>Decision 1: stop order via registration order. The Generic Host stops IHostedServices sequentially in REVERSE registration order, so for the
    /// host to stop the schedulers first and let each worker drain its queue, every send-worker descriptor must precede every scheduler descriptor. This test
    /// builds a provider with the REAL registration extensions (2-instance config) in Program.cs's exact relative sequence and pins that ordering — relative
    /// positions only, never absolute ones (impact table: not brittle to unrelated reordering).</summary>
    [Fact]
    public void Shutdown_RegistrationOrder_WorkersRegisteredBeforeSchedulers_StopOrderPinned()
    {
        var config = new NewsquackConfig { Instances = [Instance("inst-a"), Instance("inst-b")] };   // two configured instances — multi-instance mode is the point (AGENTS.md)

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNewsquackHttpClients(new HttpSectionConfig());   // T-005: the named clients every piece below resolves — exactly Program.cs's wiring
        services.AddFeedFetching();                                  // T-010 — the scheduler resolves IFeedFetcher at construction
        services.AddFeedParsing();                                   // T-012 — the scheduler resolves IFeedParser at construction
        services.AddScoped<LogEnricher>();                           // T-004 — same as Program.cs (resolved per scope at runtime, not construction)
        services.AddSingleton<NewsquackConfig>(config);              // T-024: the validated config singleton — same as Program.cs
        services.AddSingleton<IStateStore>(new RecordingStateStore());   // dedup engine + tracker + sink persist through the frozen contract — the recording fake is enough for wiring
        services.AddDedupEngine();                                   // T-014 — same as Program.cs
        services.AddPostPipeline();                                  // T-024: runner + stages + send-queue registry — same as Program.cs
        services.AddFeedErrorTracking();                             // T-016 — the scheduler resolves IFeedErrorTracker at construction

        // The EXACT relative sequence of Program.cs on both host paths (T-031 decision 1): dead-lettering, then workers, then scheduling.
        services.AddDeadLettering(config);
        services.AddSendWorkers(config);
        services.AddInstanceScheduling(config);

        using var provider = services.BuildServiceProvider();

        // GetServices enumerates in registration order — so every worker must precede every scheduler for the host's reverse-order stop to drain after the schedulers stopped.
        var hosted = provider.GetServices<IHostedService>().ToList();
        var workerIndexes = hosted.Select((service, index) => new { service, index }).Where(x => x.service is SendWorker).Select(x => x.index).ToList();
        var schedulerIndexes = hosted.Select((service, index) => new { service, index }).Where(x => x.service is InstanceScheduler).Select(x => x.index).ToList();

        Assert.Equal(2, workerIndexes.Count);      // one worker per configured instance — the full set was registered...
        Assert.Equal(2, schedulerIndexes.Count);   // ...and one scheduler per configured instance (TryAddEnumerable semantics would have dropped instances)
        Assert.True(workerIndexes.Max() < schedulerIndexes.Min(),
            $"every send worker (indexes {string.Join(", ", workerIndexes)}) must be registered before every scheduler (indexes {string.Join(", ", schedulerIndexes)}): the host stops in reverse registration order, so schedulers stop first and workers drain after");
    }

    private static InstanceConfig Instance(string id) => new()
    {
        Id = id,
        CheckIntervalMinutes = 15,   // unused here — the registration only reads Id + Discord (same as SendWorkerTests' wiring helper)
        Feeds = [new FeedConfig { Url = $"https://example.com/{id}.xml" }],
        Discord = new DiscordTargetConfig { WebhookUrl = "https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWx" },
    };

    /// <summary>Decision 2: three deliverable posts enqueued, cancel as soon as the worker is provably running ⇒ whatever is still queued is drained inside a
    /// generous window — all sent + confirmed, worker exits. (Cancelling within ~1 ms of StartAsync would race the thread-pool start of ExecuteAsync itself,
    /// so the test waits for the first send before cancelling — see the comment in the test body.)</summary>
    [Fact]
    public async Task Worker_CancellationWithPendingPosts_DrainsAllAndConfirmsEach()
    {
        var queue = new BoundedSendQueue(capacity: 5);   // room for all three pending posts — tests never use the default 100
        var target = new PerItemScriptedTarget(
            ("item-a", _ => TargetResult.Delivered()),
            ("item-b", _ => TargetResult.Delivered()),
            ("item-c", _ => TargetResult.Delivered()));   // always Delivered

        using var harness = CreateWorker(target, queue, drainTimeout: TimeSpan.FromSeconds(10));   // generous window — every post must fit inside it
        using var cts = new CancellationTokenSource();

        await harness.Worker.StartAsync(cts.Token);
        await queue.EnqueueAsync(Post("item-a"));
        await queue.EnqueueAsync(Post("item-b"));
        await queue.EnqueueAsync(Post("item-c"));   // three deliverable posts enqueued...

        // Wait until the worker has actually started (BackgroundService runs ExecuteAsync on a thread-pool task; cancelling within ~1 ms of StartAsync would
        // preempt it before its first line — .NET 10's Task.Run(action, token) returns an already-canceled task in that race). Once A is sent, the loop is provably running.
        await WaitUntilAsync(() => target.SentCountFor("item-a") >= 1, "the worker to start processing (first post sent)");

        cts.Cancel();   // ...then the host stops — whatever is still queued must be drained, not lost (decision 2)

        await harness.Worker.StopAsync(CancellationToken.None);   // returns only after the drain finished — a clean, bounded shutdown

        Assert.Equal(1, target.SentCountFor("item-a"));                              // every pending post was sent exactly once...
        Assert.Equal(1, target.SentCountFor("item-b"));                             // ...no retries on Delivered...
        Assert.Equal(1, target.SentCountFor("item-c"));                             // ...and confirmed (nothing dropped inside the window)
        Assert.Equal(3, harness.Store.MarkDeliveredCalls.Count);                    // all three confirmed by their stable dedup keys...
        Assert.Contains(harness.Store.MarkDeliveredCalls, c => c.ItemId == "item-a");   // each of the three...
        Assert.Contains(harness.Store.MarkDeliveredCalls, c => c.ItemId == "item-b");   // ...in order of arrival (single reader is sequential)
        Assert.Contains(harness.Store.MarkDeliveredCalls, c => c.ItemId == "item-c");

        var finished = harness.Capture.Find(LogLevel.Information, "Shutdown drain finished", "inst-1")!;   // ! safe: the worker always logs its drain outcome
        Assert.Contains("0 dropped unconfirmed", finished.Message);         // and the end log says nothing was dropped (decision 2's Info pair)
    }

    /// <summary>Decision 2: target always Retryable with a Retry-After FAR beyond the small drain deadline ⇒ posts are never confirmed, the worker exits ≈ the
    /// deadline later (elapsed bounds), no crash. Deterministic setup: post A is dequeued while the worker is provably running and ABSORBS the cancellation in its
    /// 60 s retry wait; post B is still in the queue when the drain starts, so it — not A — exercises the deadline. The lower bound discriminates the token choice:
    /// with the already-cancelled stopping token B's retry wait would end immediately; only a DRAIN-DEADLINE token holds until the window expires.</summary>
    [Fact]
    public async Task Worker_DrainDeadlineExpires_MidRetryPostDroppedUnconfirmed()
    {
        var queue = new BoundedSendQueue(capacity: 2);   // A (decoy, absorbed by the normal loop) + B (the drain's subject) — both must fit
        var target = new PerItemScriptedTarget(
            ("item-a", _ => TargetResult.Retryable(TimeSpan.FromSeconds(60), "Discord rate limit")),   // always Retryable with a 60 s floor — far beyond the window
            ("item-b", _ => TargetResult.Retryable(TimeSpan.FromSeconds(60), "Discord rate limit")));

        using var harness = CreateWorker(target, queue, backoffBase: TimeSpan.FromMilliseconds(10), jitterSource: () => 1.0, drainTimeout: TimeSpan.FromMilliseconds(300));
        using var cts = new CancellationTokenSource();

        await queue.EnqueueAsync(Post("item-a"));   // the decoy — dequeued once the worker starts...
        await queue.EnqueueAsync(Post("item-b"));   // ...and still in the queue when the drain starts (single reader is sequential)

        await harness.Worker.StartAsync(cts.Token);
        await WaitUntilAsync(() => target.SentCountFor("item-a") >= 1, "the decoy post to be dequeued and parked in its 60 s retry wait");

        var startedAt = Stopwatch.StartNew();
        cts.Cancel();   // the host stops — A is dropped by the normal loop; B must now be owned by the drain
        await harness.Worker.StopAsync(CancellationToken.None);   // must return ≈ the deadline later — not at 60 s (RetryAfter) and not at 0 ms (stopping token)
        startedAt.Stop();

        Assert.Equal(1, target.SentCountFor("item-a"));              // A: exactly one attempt — its retry wait never completed...
        Assert.Equal(1, target.SentCountFor("item-b"));              // B: exactly one attempt — the drain's deadline cut off its retry wait too...
        Assert.Empty(harness.Store.MarkDeliveredCalls);              // ...and NOTHING was confirmed (at-least-once: the next start re-delivers them)...
        Assert.Null(harness.Capture.Find(LogLevel.Error, "Dead letter"));   // ...and no dead-letter decision — the deadline is not a failure...
        Assert.NotNull(harness.Capture.Find(LogLevel.Debug, "item-b"));     // ...but B's drop left a Debug trace (decision 2)

        var elapsed = startedAt.Elapsed;
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(250), $"worker exited after {elapsed} — the drain must hold until its 300 ms deadline (B's retry wait is supervised by the DRAIN token, not the already-cancelled stopping token)");
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"worker took {elapsed} — far beyond the 300 ms drain window: no extra attempts, no dead-letter");
    }

    /// <summary>Decision 2's drain-token choice pinned from the other side: B's sequence is [429 Retry-After 100 ms, then 204], cancel before B is processed (A absorbs
    /// it), drain 2 s ⇒ B's retry wait completes INSIDE the window and B is delivered + confirmed. With the stopping token (already cancelled) the wait would OCE
    /// immediately and B would be dropped unconfirmed — which is exactly what this test forbids.</summary>
    [Fact]
    public async Task Worker_DrainLetsRetryWaitsComplete_WithinDeadlineDeliveredAndConfirmed()
    {
        var queue = new BoundedSendQueue(capacity: 2);   // A (decoy) + B (the drain's subject) — both must fit
        var target = new PerItemScriptedTarget(
            ("item-a", _ => TargetResult.Retryable(TimeSpan.FromSeconds(60), "Discord rate limit")),   // A: always Retryable — it absorbs the cancellation in its retry wait
            ("item-b", attempt => attempt == 0 ? TargetResult.Retryable(TimeSpan.FromMilliseconds(100), "HTTP 429") : TargetResult.Delivered()));   // B: fail once with a 100 ms floor, then deliver

        using var harness = CreateWorker(target, queue, backoffBase: TimeSpan.FromMilliseconds(10), jitterSource: () => 1.0, drainTimeout: TimeSpan.FromSeconds(2));   // the jitter candidate (10 ms) is far below B's floor; the 2 s window covers it
        using var cts = new CancellationTokenSource();

        await queue.EnqueueAsync(Post("item-a"));   // the decoy — dequeued once the worker starts...
        await queue.EnqueueAsync(Post("item-b"));   // ...and still in the queue when the drain starts (single reader is sequential)

        await harness.Worker.StartAsync(cts.Token);
        await WaitUntilAsync(() => target.SentCountFor("item-a") >= 1, "the decoy post to be dequeued and parked in its 60 s retry wait");

        var startedAt = Stopwatch.StartNew();
        cts.Cancel();   // cancel BEFORE B is processed — only a DRAIN-deadline token can let B's retry wait finish (pins decision 2's token choice)
        await harness.Worker.StopAsync(CancellationToken.None);   // returns only after the drain delivered + confirmed B (or dropped it — which is what this test forbids)
        startedAt.Stop();

        Assert.Equal(1, target.SentCountFor("item-a"));                        // A: exactly one attempt — absorbed by the normal loop's drop...
        Assert.Equal(2, target.SentCountFor("item-b"));                        // B: one retry — then delivered...
        var confirm = Assert.Single(harness.Store.MarkDeliveredCalls);         // ...and CONFIRMED inside the window (not dropped unconfirmed)...
        Assert.Equal("item-b", confirm.ItemId);                                // ...for the right item (A was never confirmed)...
        Assert.Null(confirm.NewFeedState);                                     // ...with T-021 decision 6's null newFeedState (unchanged)

        var elapsed = startedAt.Elapsed;
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(100), $"drain finished in {elapsed} — the 100 ms RetryAfter floor is absolute: never less");
        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"drain took {elapsed} — far beyond the 100 ms wait + delivery: no extra attempts");
    }

    /// <summary>Decision 5: "at start re-enqueue the unconfirmed ones" is PINNED, not rebuilt. One cycle enqueues posts and nothing is confirmed; a "restart" = fresh
    /// queue + fresh scheduler over the SAME store ⇒ T-024's immediate first cycle re-parses the feed and T-014's persistent dedup lets every never-confirmed item
    /// through again, so the unconfirmed posts reappear in the new queue. No replay mechanism exists — this test only proves the combination works.</summary>
    [Fact]
    public async Task Startup_AfterCrash_UnconfirmedPostsReenqueuedByFirstCycle()
    {
        using var harness = SchedulerTestHarness.Create(InstanceId, TimeSpan.FromSeconds(5), ["/feed.xml"]);   // long interval ⇒ exactly one cycle per run in this window

        harness.Server.Given(Request.Create().WithPath("/feed.xml"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(RssTwoItems));   // two items — both must flow through dedup → image → send into the instance's queue

        // --- first run: one cycle enqueues both posts; no worker runs, so NOTHING is confirmed ---
        using var cts1 = new CancellationTokenSource();
        var scheduler1 = harness.BuildScheduler();
        await scheduler1.StartAsync(cts1.Token);

        try
        {
            var queue1 = (BoundedSendQueue)harness.Registry.For(InstanceId);   // the SAME queue SendStage enqueues into
            var firstRun = await CollectAsync(queue1, 2);

            Assert.Equal(2, firstRun.Count);   // both posts reached the queue on the immediate first cycle...
        }
        finally
        {
            cts1.Cancel();
            await scheduler1.StopAsync(CancellationToken.None);   // "crash": the process exits with unconfirmed posts still queued (nothing was ever confirmed)
        }

        Assert.False(await harness.Store.IsDeliveredAsync(InstanceId, "https://example.com/first"));    // ...and nothing was confirmed in the store...
        Assert.False(await harness.Store.IsDeliveredAsync(InstanceId, "https://example.com/second"));   // ...the crash happened before any confirm

        // --- restart: fresh queue + fresh scheduler over the SAME store ⇒ the first cycle re-parses and the unconfirmed posts reappear ---
        var (scheduler2, registry2) = harness.BuildRestartedScheduler();
        using var cts2 = new CancellationTokenSource();
        await scheduler2.StartAsync(cts2.Token);

        try
        {
            var queue2 = (BoundedSendQueue)registry2.For(InstanceId);   // the FRESH queue — the old one is gone with the old process
            var secondRun = await CollectAsync(queue2, 2);

            Assert.Contains(secondRun, p => p.ItemId == "https://example.com/first");    // the unconfirmed posts reappear in the new queue...
            Assert.Contains(secondRun, p => p.ItemId == "https://example.com/second");   // ...via T-024's immediate first cycle + T-014's persistent dedup (decision 5)
        }
        finally
        {
            cts2.Cancel();
            await scheduler2.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, harness.Logs.CountAt(LogLevel.Error));   // a healthy restart leaves no Error logs anywhere in either run
    }

    /// <summary>Decision 3: temp DB with written rows (WAL non-empty) ⇒ StopAsync truncates the -wal file to 0 bytes and every row stays readable through a fresh
    /// connection. Mirrors AddStateStore's wiring exactly (factory-created contexts + the init interceptor on the same connection string).</summary>
    [Fact]
    public async Task CheckpointService_Stop_TruncatesWalAndDataStaysReadable()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"newsquack-t031-{Guid.NewGuid():N}.db");

        try
        {
            // Exactly AddStateStore's wiring (T-015): factory-created contexts over the production-shaped connection string + the init interceptor.
            var connectionString = $"Data Source={dbPath};Cache=Shared;Default Timeout=5";
            var services = new ServiceCollection();
            services.AddDbContextFactory<AppDbContext>((_, options) =>
                options.UseSqlite(connectionString).AddInterceptors(new SqliteConnectionInitInterceptor()));

            using (var provider = services.BuildServiceProvider())
            {
                var contextFactory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();

                // Write rows while WAL is active so the -wal file is non-empty before shutdown...
                await using (var context = await contextFactory.CreateDbContextAsync())
                {
                    await context.Database.EnsureCreatedAsync();   // schema straight from the model — a temp DB needs no migrations for this test
                    context.DeliveredItems.Add(new DeliveredItem
                    {
                        InstanceId = "inst-a", ItemId = "item-1", FeedUrl = "https://example.com/feed.xml", Title = "t1",
                        DeliveredAtUtc = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc),   // UTC only (AGENTS.md)
                    });
                    context.DeliveredItems.Add(new DeliveredItem
                    {
                        InstanceId = "inst-a", ItemId = "item-2", FeedUrl = "https://example.com/feed.xml", Title = "t2",
                        DeliveredAtUtc = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc),   // UTC only (AGENTS.md)
                    });
                    await context.SaveChangesAsync();
                }

                // ...and keep one more connection open (a long-lived reader in production terms): the WAL is only unlinked when the LAST connection closes.
                using var keeper = new SqliteConnection(connectionString);
                keeper.Open();

                Assert.True(File.Exists(dbPath + "-wal") && new FileInfo(dbPath + "-wal").Length > 0, "the -wal file must exist and be non-empty before the checkpoint — otherwise this test cannot observe it");

                var capture = new LogCapture();
                var service = new StoreCheckpointService(contextFactory, capture.For<StoreCheckpointService>());

                // The host hands StopAsync an ALREADY-CANCELED stopping token — the checkpoint must still run (issue #48 regression pin: using the
                // passed token canceled the very first await and the checkpoint never executed in production).
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await service.StopAsync(cancelled.Token);   // no-op start (decision 3) — all work happens here

                // The WAL is truncated to 0 bytes (TRUNCATE mode)...
                Assert.True(File.Exists(dbPath + "-wal"), "the -wal file must still exist after a TRUNCATE checkpoint (it is truncated, not deleted)");
                Assert.Equal(0, new FileInfo(dbPath + "-wal").Length);

                // ...and every row is still readable through a FRESH connection.
                await using (var context = await contextFactory.CreateDbContextAsync())
                    Assert.Equal(2, await context.DeliveredItems.CountAsync());

                // The result row produced one structured Info log (decision 3).
                var info = capture.Find(LogLevel.Information, "WAL checkpoint on shutdown completed")!;   // ! safe: the pragma returned its row — asserted above by the file state
                Assert.Contains("busy=0", info.Message);   // not busy — nothing else was touching the store at shutdown (stop order guarantees it)
            }
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = dbPath + suffix;
                if (File.Exists(file)) File.Delete(file);   // temp-file pattern (PersistenceRoundTripTests) — Linux unlinks open files fine
            }
        }
    }

    /// <summary>Decision 4: the internal static parse of NEWSQUACK_SHUTDOWN_DRAIN_TIMEOUT_SECONDS — null/"" ⇒ 30 s (no warning); "abc"/"0"/"-5" ⇒ 30 s + one Warning each;
    /// "7" ⇒ exactly 7 s. Unit-tested without mutating process env (T-022 pattern).</summary>
    [Fact]
    public void Registration_ParseDrainTimeout_MissingOrInvalid_ReturnsDefaultThirtySeconds()
    {
        var capture = new LogCapture();
        var logger = capture.For<SendWorker>();

        // missing/empty ⇒ the 30 s default, NO warning (an unset operational env var is normal)
        Assert.Equal(TimeSpan.FromSeconds(30), SendWorkerRegistration.ParseDrainTimeout(null, logger));
        Assert.Equal(TimeSpan.FromSeconds(30), SendWorkerRegistration.ParseDrainTimeout("", logger));
        Assert.Equal(TimeSpan.FromSeconds(30), SendWorkerRegistration.ParseDrainTimeout("   ", logger));

        // invalid / non-positive ⇒ the 30 s default + a Warning each (the host keeps a sane window)
        Assert.Equal(TimeSpan.FromSeconds(30), SendWorkerRegistration.ParseDrainTimeout("abc", logger));
        Assert.Equal(TimeSpan.FromSeconds(30), SendWorkerRegistration.ParseDrainTimeout("0", logger));
        Assert.Equal(TimeSpan.FromSeconds(30), SendWorkerRegistration.ParseDrainTimeout("-5", logger));

        // a positive integer ⇒ exactly that many seconds
        Assert.Equal(TimeSpan.FromSeconds(7), SendWorkerRegistration.ParseDrainTimeout("7", logger));

        var warnings = capture.Snapshot().Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(3, warnings.Count);   // exactly one warning per invalid value — "abc", "0", "-5" (missing/empty and valid values stay silent)
        Assert.All(warnings, w => Assert.Contains("NEWSQUACK_SHUTDOWN_DRAIN_TIMEOUT_SECONDS", w.Message));   // the env var is named in every warning (operational pattern)
    }

    /// <summary>Non-blocking collection of up to <paramref name="count"/> posts from a queue with a generous timeout (same pattern as InstanceSchedulerTests).</summary>
    private static async Task<List<Post>> CollectAsync(BoundedSendQueue queue, int count)
    {
        var collected = new List<Post>();
        var sw = Stopwatch.StartNew();

        while (collected.Count < count && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (queue.TryRead(out var post)) collected.Add(post);   // non-blocking drain — posts arrive as the cycle parses them
            else await Task.Delay(20, CancellationToken.None);
        }

        return collected;
    }
}
