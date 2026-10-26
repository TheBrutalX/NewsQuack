using System.Diagnostics;   // Stopwatch — deterministic-timing assertions (pinned jitter, not wall-clock luck)
using System.Text.Json;     // JsonDocument — asserting key fields in the persisted dead-letter payload (T-022)
using NewsQuack.App.Configuration;   // NewsQuackConfig / InstanceConfig / FeedConfig / DiscordTargetConfig — the registration is built from config
using NewsQuack.App.Http;            // AddNewsQuackHttpClients — the "discord" named client the target resolves (T-005)
using NewsQuack.App.Logging;         // LogEnricher — registered Scoped, resolved per post scope by the worker (decision 5)
using NewsQuack.App.Pipeline;        // BoundedSendQueue / DedupEngine / SendQueueRegistry — the real pieces the worker consumes
using NewsQuack.App.Sending;         // SendWorker + AddSendWorkers — the unit under test
using NewsQuack.Core;                // Post / INotificationTarget / IDedupEngine / TargetResult — the frozen contracts
using Microsoft.Extensions.DependencyInjection;   // ServiceCollection for the scoped LogEnricher scope factory + the registration test
using Microsoft.Extensions.Hosting;   // IHostedService — asserting one worker per configured instance
using Microsoft.Extensions.Logging;   // LogLevel — log assertions
using WireMock.RequestBuilders;       // Request — stubbing each instance's webhook server
using WireMock.ResponseBuilders;      // Response — 204 answers for the wiring test
using WireMock.Server;                // WireMockServer — two dynamic-port servers, one per instance (CI-safe)
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-021: the per-instance single-reader send worker — retry policy with the RetryAfter floor + full jitter, MaxAttempts=5 dead-letter decision
/// point (T-022: persisted via the real sink + structured Warning log), confirm-only-after-Delivered with null newFeedState, per-post enrichment,
/// target exceptions as transient failures, and clean cancellation. Uses a scriptable fake INotificationTarget (per-attempt outcomes), the REAL
/// BoundedSendQueue (small capacity), the REAL DedupEngine over the recording store fake (confirm arguments assertable), the REAL DeadLetterSink
/// over that same recording store (dead-letter calls assertable — T-022), and the test-only ctor parameters for deterministic timing.
/// </summary>
public class SendWorkerTests
{
    private const string InstanceId = "inst-1";
    private const string FeedUrl = "https://example.com/feed.xml";

    /// <summary>Polls a condition every 5 ms until it holds or the (generous) timeout passes — worker work happens on its own task.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 1000 && !condition(); i++)   // up to ~5 s — far beyond every pinned wait in these tests
            await Task.Delay(5);

        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    private static Post Post(string itemId, string title = "A title") => new()
    {
        Id = Guid.NewGuid().ToString(),
        ItemId = itemId,
        Title = title,
        PublishedAt = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero),   // always UTC
        SourceUrl = FeedUrl,
    };

    /// <summary>Assembles the worker around the REAL production pieces: real BoundedSendQueue (small capacity), real DedupEngine over the recording
    /// store fake (confirm arguments assertable), a scoped-LogEnricher scope factory from a minimal container — plus the test-only timing injectables.</summary>
    private static WorkerHarness CreateWorker(
        INotificationTarget target, BoundedSendQueue queue, TimeSpan? backoffBase = null, TimeSpan? backoffCap = null, Func<double>? jitterSource = null)
    {
        var capture = new LogCapture();
        var store = new RecordingStateStore();

        // Scoped LogEnricher (T-004): the worker creates one scope per post — this minimal container supplies it (same pattern as SchedulerTestHarness).
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
            backoffBase: backoffBase, backoffCap: backoffCap, jitterSource: jitterSource);   // test-only injectables — deterministic timing (decision 1)

        return new WorkerHarness(worker, store, capture, provider);
    }

    private sealed class WorkerHarness : IDisposable
    {
        private readonly ServiceProvider _provider;

        public WorkerHarness(SendWorker worker, RecordingStateStore store, LogCapture capture, ServiceProvider provider)
        {
            Worker = worker;   // every test stops the worker before it ends — Dispose then only releases the scope factory (same pattern as SchedulerTestHarness)
            Store = store;
            Capture = capture;
            _provider = provider;
        }

        public SendWorker Worker { get; }
        public RecordingStateStore Store { get; }
        public LogCapture Capture { get; }

        public void Dispose() => _provider.Dispose();   // the worker is already stopped by the test — disposing its scope factory is safe then
    }

    [Fact]
    public async Task Worker_Delivered_ConfirmsWithExactArgumentsAndMovesToNextPost()
    {
        var queue = new BoundedSendQueue(capacity: 2);   // small capacity — tests never use the default 100
        var target = new ScriptedTarget([p => TargetResult.Delivered()]);   // always Delivered (a single step repeats)

        using var harness = CreateWorker(target, queue);
        using var cts = new CancellationTokenSource();

        await harness.Worker.StartAsync(cts.Token);
        await queue.EnqueueAsync(Post("item-a", "Title A"));
        await queue.EnqueueAsync(Post("item-b", "Title B"));

        await WaitUntilAsync(() => harness.Store.MarkDeliveredCalls.Count == 2, "both posts to be confirmed");
        await harness.Worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, target.SentCount);   // each post sent exactly once — no retries on Delivered

        var first = harness.Store.MarkDeliveredCalls[0];
        Assert.Equal(InstanceId, first.InstanceId);                  // the worker's own instance id...
        Assert.Equal(FeedUrl, first.FeedUrl);                        // ...post.SourceUrl as the feed url...
        Assert.Equal("item-a", first.ItemId);                        // ...the post's stable dedup key...
        Assert.Equal(TimeSpan.Zero, first.DeliveredAtUtc.Offset);   // UTC only (AGENTS.md)...
        Assert.Null(first.NewFeedState);                             // ...and newFeedState = null — decision 6: feed state untouched in v1.0

        var second = harness.Store.MarkDeliveredCalls[1];
        Assert.Equal("item-b", second.ItemId);                       // and the worker moved on to the next post
    }

    [Fact]
    public async Task Worker_RetryableThenDelivered_WaitsJitteredBackoffAndConfirmsOnce()
    {
        var queue = new BoundedSendQueue(capacity: 1);
        var target = new ScriptedTarget([
            p => TargetResult.Retryable(null, "HTTP 503"),   // attempt 1 — transient...
            p => TargetResult.Delivered(),                   // ...attempt 2 delivers
        ]);

        using var harness = CreateWorker(target, queue, backoffBase: TimeSpan.FromMilliseconds(50), jitterSource: () => 1.0);   // jitter pinned to the max ⇒ first wait is exactly base (50 ms)
        using var cts = new CancellationTokenSource();

        var startedAt = Stopwatch.StartNew();
        await harness.Worker.StartAsync(cts.Token);
        await queue.EnqueueAsync(Post("item-a"));

        await WaitUntilAsync(() => harness.Store.MarkDeliveredCalls.Count == 1, "the post to be confirmed");
        startedAt.Stop();
        await harness.Worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, target.SentCount);                                   // one retry — then delivered
        var elapsed = startedAt.Elapsed;
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(50), $"waited only {elapsed} — the jittered backoff (50 ms at jitter 1.0) must elapse before attempt 2");
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"waited {elapsed} — far beyond the pinned 50 ms wait: no extra attempts, no dead-letter");

        var confirm = harness.Store.MarkDeliveredCalls[0];
        Assert.Equal("item-a", confirm.ItemId);                              // exactly one confirm (the single entry above)...
        Assert.Null(confirm.NewFeedState);                                   // ...with decision 6's null newFeedState
    }

    [Fact]
    public async Task Worker_RetryableWithRetryAfter_NeverWaitsLessThanRetryAfter()
    {
        var queue = new BoundedSendQueue(capacity: 1);
        var target = new ScriptedTarget([
            p => TargetResult.Retryable(TimeSpan.FromMilliseconds(300), "Discord rate limit"),   // attempt 1 — a 300 ms Retry-After floor...
            p => TargetResult.Delivered(),                                                       // ...attempt 2 delivers
        ]);

        using var harness = CreateWorker(target, queue, backoffBase: TimeSpan.FromMilliseconds(10), jitterSource: () => 1.0);   // the jitter candidate is only 10 ms — far below the floor
        using var cts = new CancellationTokenSource();

        var startedAt = Stopwatch.StartNew();
        await harness.Worker.StartAsync(cts.Token);
        await queue.EnqueueAsync(Post("item-a"));

        await WaitUntilAsync(() => harness.Store.MarkDeliveredCalls.Count == 1, "the post to be confirmed");
        startedAt.Stop();
        await harness.Worker.StopAsync(CancellationToken.None);

        Assert.True(startedAt.Elapsed >= TimeSpan.FromMilliseconds(300), $"waited only {startedAt.Elapsed} — the RetryAfter floor is absolute: never less than 300 ms");
        Assert.Equal(2, target.SentCount);   // exactly one retry — the floor delayed it, it did not multiply attempts
    }

    [Fact]
    public async Task Worker_MaxAttemptsExhausted_DeadLettersWithClassifiedErrorAndContinuesToNextPost()
    {
        var queue = new BoundedSendQueue(capacity: 2);
        var target = new ScriptedTarget([p => TargetResult.Retryable(null, "HTTP 503")]);   // always Retryable — the budget must exhaust

        using var harness = CreateWorker(target, queue, backoffBase: TimeSpan.FromMilliseconds(1), jitterSource: () => 1.0);   // waits of 1+2+4+8 ms — fast and deterministic
        using var cts = new CancellationTokenSource();

        await harness.Worker.StartAsync(cts.Token);
        await queue.EnqueueAsync(Post("item-a", "Failing post"));
        await queue.EnqueueAsync(Post("item-b", "Next post"));

        // item-a's dead-letter decision appears first... (T-022: the dead-letter log now lives in the sink as a Warning, not an Error)
        await WaitUntilAsync(() => harness.Capture.Find(LogLevel.Warning, "Dead letter", "item-a") is not null, "the dead-letter decision for item-a");

        var deadLetter = harness.Capture.Find(LogLevel.Warning, "Dead letter", "item-a")!;   // ! safe: the wait above guarantees the entry exists
        Assert.Equal(5, target.SentCountFor("item-a"));   // EXACTLY MaxAttempts for the failing post — no sixth try...

        // ...and the worker CONTINUES to the next post (which dead-letters too — the target always fails).
        await WaitUntilAsync(() => harness.Capture.Find(LogLevel.Warning, "Dead letter", "item-b") is not null, "the next post to be processed");

        Assert.Contains("attempts made 5", deadLetter.Message);   // attempts made...
        Assert.Contains("HTTP 503", deadLetter.Message);          // ...and the last error as reported by the target (decision 4)...
        Assert.Contains(FeedUrl, deadLetter.Message);             // ...and the feed url — one structured Warning line carries everything T-022 persists...

        await harness.Worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, harness.Store.RecordDeadLetterCalls.Count);   // ...and BOTH dead letters were persisted through the sink (T-022 decision 6)

        Assert.DoesNotContain(harness.Store.MarkDeliveredCalls, c => c.ItemId == "item-a");   // never confirmed — at-least-once: re-polling re-delivers it
    }

    [Fact]
    public async Task Worker_PermanentFailure_DeadLettersImmediatelyWithoutRetry()
    {
        var queue = new BoundedSendQueue(capacity: 1);
        var target = new ScriptedTarget([p => TargetResult.PermanentFailure("403: forbidden")]);   // permanent — the retry budget is skipped entirely

        using var harness = CreateWorker(target, queue);
        using var cts = new CancellationTokenSource();

        await harness.Worker.StartAsync(cts.Token);
        await queue.EnqueueAsync(Post("item-a", "Rejected post"));

        await WaitUntilAsync(() => harness.Capture.Find(LogLevel.Warning, "Dead letter", "item-a") is not null, "the immediate dead-letter");   // T-022: Warning via the sink, not an Error from the worker
        await harness.Worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, target.SentCount);   // EXACTLY one call — PermanentFailure dead-letters immediately (decision 3)
        Assert.DoesNotContain(harness.Store.MarkDeliveredCalls, c => c.ItemId == "item-a");   // never confirmed...
        Assert.Single(harness.Store.RecordDeadLetterCalls);   // ...but persisted through the sink as a dead letter (T-022 decision 6)
    }

    [Fact]
    public async Task Worker_MaxAttemptsExhausted_PersistsDeadLetterWith5xxCategoryAndFullPayload()
    {
        var queue = new BoundedSendQueue(capacity: 1);
        var target = new ScriptedTarget([p => TargetResult.Retryable(null, "HTTP 503")]);   // always 503 — the retry budget must exhaust

        using var harness = CreateWorker(target, queue, backoffBase: TimeSpan.FromMilliseconds(1), jitterSource: () => 1.0);   // waits of 1+2+4+8 ms — fast and deterministic
        using var cts = new CancellationTokenSource();

        await harness.Worker.StartAsync(cts.Token);
        await queue.EnqueueAsync(Post("item-a", "Failing post"));

        await WaitUntilAsync(() => harness.Store.RecordDeadLetterCalls.Count == 1, "the dead letter to be persisted");
        await harness.Worker.StopAsync(CancellationToken.None);

        var data = harness.Store.RecordDeadLetterCalls[0];   // ! safe: the wait above guarantees the entry
        Assert.Equal(InstanceId, data.InstanceId);           // the sink's instance id...
        Assert.Equal(FeedUrl, data.FeedUrl);                 // ...post.SourceUrl as the feed url...
        Assert.Equal("item-a", data.ItemId);                 // ...the post's stable dedup key...
        Assert.Equal(SendWorker.MaxAttempts, data.Attempts); // attempts = 5 — exactly the exhausted budget (decision 4)...
        Assert.Equal("5xx", data.ErrorCategory);             // ...category "5xx" — T-020's "HTTP 503" shape (classifier decision 3)...
        Assert.Equal("HTTP 503", data.LastError);            // ...and the last error as reported by the target...
        Assert.True(data.FailedAtUtc.Offset == TimeSpan.Zero);   // UTC only (AGENTS.md)

        // The FULL post payload is persisted as JSON — key fields present.
        using var document = JsonDocument.Parse(data.PayloadJson);
        var root = document.RootElement;
        Assert.Equal("item-a", root.GetProperty("ItemId").GetString());
        Assert.Equal("Failing post", root.GetProperty("Title").GetString());

        // The decision left exactly ONE structured Warning (the sink's) — no Error-level dead-letter log remains (T-022 decision 6).
        Assert.NotNull(harness.Capture.Find(LogLevel.Warning, "Dead letter", "item-a"));
        Assert.Null(harness.Capture.Find(LogLevel.Error, "Dead letter"));

        Assert.Empty(harness.Store.MarkDeliveredCalls);   // never confirmed — at-least-once: re-polling re-delivers it
    }

    [Fact]
    public async Task Worker_PermanentFailure_PersistsDeadLetterWith400PayloadCategory()
    {
        var queue = new BoundedSendQueue(capacity: 1);
        var target = new ScriptedTarget([p => TargetResult.PermanentFailure("403: forbidden")]);   // permanent — the retry budget is skipped entirely

        using var harness = CreateWorker(target, queue);
        using var cts = new CancellationTokenSource();

        await harness.Worker.StartAsync(cts.Token);
        await queue.EnqueueAsync(Post("item-a", "Rejected post"));

        await WaitUntilAsync(() => harness.Store.RecordDeadLetterCalls.Count == 1, "the immediate dead letter");
        await harness.Worker.StopAsync(CancellationToken.None);

        var data = harness.Store.RecordDeadLetterCalls[0];   // ! safe: the wait above guarantees the entry
        Assert.Equal(1, data.Attempts);                      // exactly one attempt — PermanentFailure dead-letters immediately (decision 3)...
        Assert.Equal("400-payload", data.ErrorCategory);     // ...category "400-payload" — v1.0's only permanent class (classifier decision 3)...
        Assert.Equal("403: forbidden", data.LastError);      // ...and the target's error string preserved verbatim

        Assert.Empty(harness.Store.MarkDeliveredCalls);   // never confirmed — at-least-once: re-polling re-delivers it
    }

    [Fact]
    public async Task Worker_ConfirmThrows_LogsErrorAndContinues_ItemNotConfirmed()
    {
        var queue = new BoundedSendQueue(capacity: 2);
        var target = new ScriptedTarget([p => TargetResult.Delivered()]);   // both posts deliver — the failure is on the CONFIRM side

        using var harness = CreateWorker(target, queue);
        using var cts = new CancellationTokenSource();

        harness.Store.MarkDeliveredException = new InvalidOperationException("sqlite busy");   // simulate a store outage at confirm time
        await harness.Worker.StartAsync(cts.Token);
        await queue.EnqueueAsync(Post("item-a"));

        await WaitUntilAsync(
            () => harness.Store.MarkDeliveredCalls.Count == 1 && harness.Capture.Find(LogLevel.Error, "sent but not confirmed", "item-a") is not null,
            "the confirm failure to be logged");

        var error = harness.Capture.Find(LogLevel.Error, "sent but not confirmed", "item-a")!;   // ! safe: the wait above guarantees the entry exists
        Assert.NotNull(error.Exception);   // structured log — the exception is passed as an argument, stack trace survives (AGENTS.md)

        // The worker SURVIVES the confirm failure and keeps processing...
        harness.Store.MarkDeliveredException = null;   // store recovers
        await queue.EnqueueAsync(Post("item-b"));
        await WaitUntilAsync(() => harness.Store.IsDelivered(InstanceId, "item-b"), "the next post to be confirmed");

        await harness.Worker.StopAsync(CancellationToken.None);

        Assert.False(harness.Store.IsDelivered(InstanceId, "item-a"));   // the store shows item-a NOT delivered — it will be re-delivered on the next cycle (at-least-once)
    }

    [Fact]
    public async Task Worker_TargetThrowsException_TreatedAsTransientAndRetried()
    {
        var queue = new BoundedSendQueue(capacity: 1);
        var target = new ScriptedTarget([
            p => throw new InvalidOperationException("socket exploded"),   // attempt 1 — the target THROWS instead of returning a result...
            p => TargetResult.Delivered(),                                 // ...attempt 2 delivers ⇒ the exception was treated as transient (Retryable)
        ]);

        using var harness = CreateWorker(target, queue);
        using var cts = new CancellationTokenSource();

        await harness.Worker.StartAsync(cts.Token);
        await queue.EnqueueAsync(Post("item-a"));

        await WaitUntilAsync(() => harness.Store.MarkDeliveredCalls.Count == 1, "the post to be confirmed");
        await harness.Worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, target.SentCount);   // the throw did not end the post's life — it was retried...

        var warning = harness.Capture.Find(LogLevel.Warning, "socket exploded")!;   // ! safe: the confirm above requires the retry, which requires this log
        Assert.NotNull(warning.Exception);   // ...and logged with the exception as argument (stack trace survives — AGENTS.md)

        Assert.Single(harness.Store.MarkDeliveredCalls);                            // and confirmed exactly once...
        Assert.Equal("item-a", harness.Store.MarkDeliveredCalls[0].ItemId);         // ...for the right item
    }

    [Fact]
    public async Task Worker_CancellationDuringRetryWait_ExitsCleanlyWithoutDeadLetterLog()
    {
        var queue = new BoundedSendQueue(capacity: 1);
        var target = new ScriptedTarget([p => TargetResult.Retryable(null, "HTTP 503")]);   // always Retryable — the worker sits in its retry wait

        using var harness = CreateWorker(target, queue, backoffBase: TimeSpan.FromSeconds(30), jitterSource: () => 1.0);   // a 30 s wait ⇒ cancellation lands inside Task.Delay
        using var cts = new CancellationTokenSource();

        await harness.Worker.StartAsync(cts.Token);   // the worker's stopping token — cancellation reaches ReadAsync / SendAsync / Task.Delay end-to-end
        await queue.EnqueueAsync(Post("item-a"));

        await WaitUntilAsync(() => target.SentCount >= 1, "the first attempt (then the worker is inside its retry wait)");
        cts.Cancel();   // cancel MID-RETRY-WAIT — decision 2: the dequeued post is dropped with a Debug log, never confirmed

        await harness.Worker.StopAsync(CancellationToken.None);   // returns only when ExecuteAsync has exited — a clean shutdown, no exception

        Assert.Equal(1, target.SentCount);                        // exactly one attempt — the second never happened...
        Assert.Empty(harness.Store.MarkDeliveredCalls);           // ...the post was never confirmed (at-least-once: the next cycle re-delivers it)...
        Assert.Null(harness.Capture.Find(LogLevel.Error, "Dead letter"));   // ...and no dead-letter decision was made — cancellation is not a failure
        Assert.NotNull(harness.Capture.Find(LogLevel.Debug, "item-a"));     // the dropped post left a Debug trace (decision 2)
    }

    [Fact]
    public async Task AddSendWorkers_RegistersOneTargetAndOneWorkerPerConfiguredInstance()
    {
        var serverA = WireMockServer.Start();   // instance A's webhook...
        var serverB = WireMockServer.Start();   // ...and instance B's — each target must hit its OWN server

        try
        {
            serverA.Given(Request.Create()).RespondWith(Response.Create().WithStatusCode(204));
            serverB.Given(Request.Create()).RespondWith(Response.Create().WithStatusCode(204));

            var config = new NewsQuackConfig { Instances = [
                Instance("inst-a", serverA.Url!),   // two configured instances — each with its own webhook URL (! safe: a started WireMockServer always has its Url set)
                Instance("inst-b", serverB.Url!),
            ]};

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNewsQuackHttpClients(new HttpSectionConfig());   // the "discord" named client the target resolves (T-005) — exactly Program.cs's wiring
            services.AddSingleton<NewsQuackConfig>(config);              // T-024: the validated config singleton — same as Program.cs
            services.AddSingleton(sp => new SendQueueRegistry(sp.GetRequiredService<NewsQuackConfig>()));   // T-024: one queue per configured instance
            services.AddSingleton<IDedupEngine>(_ => new NoopDedup());  // the worker resolves it at construction — a no-op is enough for wiring
            services.AddSingleton<IStateStore>(new RecordingStateStore());   // T-022: the sink persists through the frozen contract — the recording fake is enough for wiring
            services.AddDeadLettering(config);                          // T-022: the per-instance dead-letter sinks + notifier — part of Program.cs's exact wiring
            services.AddSendWorkers(config);                            // the exact wiring Program.cs uses on both host paths

            using var provider = services.BuildServiceProvider();

            // ONE worker per configured instance — nothing more, nothing less...
            var workers = provider.GetServices<IHostedService>().OfType<SendWorker>().ToList();
            Assert.Equal(2, workers.Count);   // 2 instances ⇒ exactly 2 SendWorkers (host-managed BackgroundServices)

            // ...and each target is built with ITS OWN instance's webhook URL — resolve the keyed per-instance targets and send through each.
            var targetA = provider.GetRequiredKeyedService<INotificationTarget>("inst-a");
            var targetB = provider.GetRequiredKeyedService<INotificationTarget>("inst-b");

            await targetA.SendAsync(Post("item-a"));   // inst-a's target posts to server A...
            await targetB.SendAsync(Post("item-b"));   // ...inst-b's to server B — no cross-instance webhook leak

            Assert.Single(serverA.LogEntries);   // A received exactly one POST — from its own instance's target...
            Assert.Single(serverB.LogEntries);   // ...and B exactly one (decision 7: per-instance factory closure, keyed singleton)
        }
        finally
        {
            serverA.Dispose();
            serverB.Dispose();
        }
    }

    private static InstanceConfig Instance(string id, string webhookUrl) => new()
    {
        Id = id,
        CheckIntervalMinutes = 15,   // unused here — the registration only reads Id + Discord (decision 7)
        Feeds = [new FeedConfig { Url = $"https://example.com/{id}.xml" }],
        Discord = new DiscordTargetConfig { WebhookUrl = webhookUrl },
    };

    /// <summary>Scriptable INotificationTarget fake: one scripted outcome per attempt (a step may throw), the LAST step repeats — so a single
    /// "always Retryable" step and a two-step "fail once, then deliver" script both work. A lock guards the counters because the worker runs on its own task.</summary>
    private sealed class ScriptedTarget : INotificationTarget
    {
        private readonly object _gate = new();
        private readonly Func<Post, TargetResult>[] _script;
        private readonly List<Post> _posts = [];

        public ScriptedTarget(IEnumerable<Func<Post, TargetResult>> script) => _script = script.ToArray();

        /// <summary>Total sends across all posts.</summary>
        public int SentCount { get { lock (_gate) return _posts.Count; } }

        /// <summary>Sends for one item id — pins "exactly MaxAttempts for THIS post" while the worker may already be on the next one.</summary>
        public int SentCountFor(string itemId) { lock (_gate) return _posts.Count(p => p.ItemId == itemId); }

        public Task<TargetResult> SendAsync(Post post, CancellationToken ct = default)
        {
            Func<Post, TargetResult> step;
            lock (_gate)
            {
                _posts.Add(post);
                step = _script[Math.Min(_posts.Count - 1, _script.Length - 1)];   // the last script entry repeats — "always X" is a one-step script
            }

            return Task.FromResult(step(post));   // a throwing step propagates OUT of SendAsync — exactly what decision 3's "any other exception" clause covers
        }
    }

    /// <summary>Minimal IDedupEngine for the wiring test — the worker resolves it at construction; no confirm is expected there.</summary>
    private sealed class NoopDedup : IDedupEngine
    {
        public Task<bool> IsDeliveredAsync(string instanceId, string itemId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> IsPendingAsync(string instanceId, string itemId, CancellationToken ct = default) => Task.FromResult(false);   // T-049: nothing in flight in the wiring test

        public Task ConfirmDeliveryAsync(string instanceId, string feedUrl, string itemId, DateTimeOffset deliveredAtUtc, FeedState? newFeedState, CancellationToken ct = default) => Task.CompletedTask;
    }
}
