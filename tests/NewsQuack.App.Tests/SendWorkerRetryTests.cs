using System.Collections.Concurrent;   // ConcurrentQueue — request-arrival timestamps recorded from the WireMock callback (server thread)
using System.Text.Json;                // JsonElement — asserting the swap direction via the logged request bodies
using NewsQuack.App.Configuration;      // DiscordTargetConfig — the target config built in-test (webhook = server URL)
using NewsQuack.App.Discord;            // DiscordNotificationTarget + HttpClientName — the REAL target under test
using NewsQuack.App.Logging;            // LogEnricher — registered Scoped, resolved per post scope by the worker (T-021 decision 5)
using NewsQuack.App.Pipeline;           // BoundedSendQueue / DedupEngine — the real pieces the worker consumes
using NewsQuack.App.Sending;            // SendWorker — under test
using NewsQuack.Core;                   // Post / IStateStore / IDedupEngine — the frozen contracts
using Microsoft.Extensions.DependencyInjection;   // ServiceCollection — minimal container: named client + scoped LogEnricher
using Microsoft.Extensions.Logging;    // LogLevel — log assertions
using WireMock;                        // IResponseMessage — the sequenced-answer callback's return type (WireMock 2.x)
using WireMock.RequestBuilders;        // Request — stubbing the webhook server
using WireMock.ResponseBuilders;       // Response — sequenced answers inside the WithCallback counter (T-020 pattern)
using WireMock.Server;                 // WireMockServer — dynamic port, CI-safe
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-044: end-to-end retry/backoff regression tests through the REAL <see cref="DiscordNotificationTarget"/> pointed at a self-hosted WireMock serving
/// SEQUENCED answers, drained by the REAL per-instance <see cref="SendWorker"/>. Pins the delivery retry behavior as a whole-system property:
/// a 429's Retry-After is an absolute floor (never waited less), transient 5xx burn exactly MaxAttempts then dead-letter with the classified error,
/// a channel-type 400 triggers exactly one internal swap and then delivers, and across all retry paths a post is sent the EXACT number of times the
/// scenario implies — confirmed exactly once, never duplicated or re-enqueued. Nothing in the path is scripted: real target + real worker + real
/// queue + real dedup engine (decision 2); only the HTTP answers are sequenced via a WithCallback counter (WireMock 2.x has no ordered stubs).
/// </summary>
public class SendWorkerRetryTests
{
    private const string InstanceId = "inst-retry";
    private const string FeedUrl = "https://example.com/feed.xml";
    private const string ItemId = "item-1";

    /// <summary>The channel-type 400 body — nested string-code signal, same constant as DiscordTargetSendTests (T-020).</summary>
    private const string ChannelTypeCodeStringBody = """{"embeds":[],"non_json_objects":[],"errors":{"thread_name":{"code":"50_071"}}}""";

    /// <summary>Small tolerance for clock granularity on the floor-based timing assertions — the property under test is "never wait LESS", so the
    /// epsilon can only shave milliseconds off a pinned floor, never seconds.</summary>
    private static readonly TimeSpan ClockEpsilon = TimeSpan.FromMilliseconds(25);

    [Fact]
    public async Task Retry_429WithRetryAfterThen204_WaitsAtLeastRetryAfterAndConfirmsExactlyOnce()
    {
        // base 50 ms pinned at jitter 1.0 ⇒ the jittered candidate (50 ms) is far below the 1 s floor — the wait is EXACTLY the Retry-After floor.
        using var host = RetryHost.Create("embed", backoffBase: TimeSpan.FromMilliseconds(50), jitterSource: () => 1.0);

        // Call 1 → 429 with a Retry-After header of 1 s; call 2 → 204 (sequenced via the callback counter).
        host.Sequence(n => n == 1
            ? Response.Create().WithStatusCode(429).WithHeader("Retry-After", "1").WithBody("{}").ResponseMessage
            : Response.Create().WithStatusCode(204).ResponseMessage);

        using var cts = new CancellationTokenSource();
        await host.Worker.StartAsync(cts.Token);
        await host.Queue.EnqueueAsync(MakePost(), cts.Token);

        await WaitUntilAsync(() => host.Store.MarkDeliveredCalls.Count == 1, "the post to be confirmed");
        await host.Worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, host.Calls);   // EXACTLY two sends — the 429 retried once, then delivered; no extra or parallel sends (decision 4)

        var gap = host.BetweenCalls(1, 2);
        Assert.True(gap >= TimeSpan.FromSeconds(1) - ClockEpsilon, $"waited only {gap} between attempts — the Retry-After floor (1 s) is absolute: never less");

        Assert.Single(host.Store.MarkDeliveredCalls);   // confirmed EXACTLY once...
        var confirm = host.Store.MarkDeliveredCalls[0]; // ! safe: Single above guarantees the entry
        Assert.Equal(InstanceId, confirm.InstanceId);                  // ...the worker's own instance id...
        Assert.Equal(FeedUrl, confirm.FeedUrl);                        // ...post.SourceUrl as the feed url...
        Assert.Equal(ItemId, confirm.ItemId);                          // ...the post's stable dedup key...
        Assert.Equal(TimeSpan.Zero, confirm.DeliveredAtUtc.Offset);   // UTC only (AGENTS.md)...
        Assert.Null(confirm.NewFeedState);                             // ...and newFeedState = null — T-021 decision 6: feed state untouched in v1.0

        Assert.Null(host.Capture.Find(LogLevel.Error, "Dead letter"));   // delivered ⇒ no dead-letter decision
    }

    [Fact]
    public async Task Retry_Only5xx_ExhaustsMaxAttempts_DeadLettersWithExactSendCount()
    {
        // base 25 ms + jitter pinned to 1.0 ⇒ waits of exactly 25+50+100+200 = 375 ms — the expected total is exact (decision 3).
        using var host = RetryHost.Create("embed", backoffBase: TimeSpan.FromMilliseconds(25), backoffCap: TimeSpan.FromSeconds(60), jitterSource: () => 1.0);

        host.Sequence(_ => Response.Create().WithStatusCode(503)   // always 503 — the retry budget must exhaust
            .WithHeader("Content-Type", "application/json").WithBody("""{"message":"Service Unavailable"}""").ResponseMessage);

        using var cts = new CancellationTokenSource();
        await host.Worker.StartAsync(cts.Token);
        await host.Queue.EnqueueAsync(MakePost(), cts.Token);

        await WaitUntilAsync(() => host.Capture.Find(LogLevel.Error, "Dead letter", ItemId) is not null, "the dead-letter decision");
        await host.Worker.StopAsync(CancellationToken.None);

        Assert.Equal(5, host.Calls);   // EXACTLY MaxAttempts sends — no sixth try, no early give-up (decision 4)

        var total = host.FirstToLast();
        Assert.True(total >= TimeSpan.FromMilliseconds(375) - ClockEpsilon, $"waited only {total} — the four pinned backoff waits sum to exactly 375 ms");
        Assert.True(total < TimeSpan.FromSeconds(10), $"waited {total} — far beyond the pinned 375 ms: no runaway retry loop");

        var deadLetter = host.Capture.Snapshot();
        Assert.Single(deadLetter, e => e.Level == LogLevel.Error && e.Message.Contains("Dead letter"));   // ONE Error dead-letter log...
        Assert.Contains("attempts made 5", deadLetter.First(e => e.Level == LogLevel.Error && e.Message.Contains("Dead letter")).Message);   // ...with attempts made...
        Assert.Contains("HTTP 503", deadLetter.First(e => e.Level == LogLevel.Error && e.Message.Contains("Dead letter")).Message);          // ...and the classified last error as reported by the target (T-020 mapping)

        Assert.Empty(host.Store.MarkDeliveredCalls);   // never confirmed — at-least-once: re-polling re-delivers it
    }

    [Fact]
    public async Task Retry_400ChannelTypeThenSwapTo204_SwappedOnceAndDeliveredOnFirstAttempt()
    {
        using var host = RetryHost.Create("forum", backoffBase: TimeSpan.FromMilliseconds(10), jitterSource: () => 1.0);   // small constants keep any (absent) retry fast

        // Call 1 → channel-type 400 (nested string-code signal); call 2 → 204 — BOTH inside the single worker attempt (the target's one-shot swap).
        host.Sequence(n => n == 1
            ? Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody(ChannelTypeCodeStringBody).ResponseMessage
            : Response.Create().WithStatusCode(204).ResponseMessage);

        using var cts = new CancellationTokenSource();
        await host.Worker.StartAsync(cts.Token);
        await host.Queue.EnqueueAsync(MakePost(), cts.Token);

        await WaitUntilAsync(() => host.Store.MarkDeliveredCalls.Count == 1, "the post to be confirmed");
        await host.Worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, host.Calls);   // EXACTLY two sends — the swap happened once inside ONE SendAsync; a worker-level retry would make it ≥ 3 (decision 4)

        Assert.True(host.EntryBody(0).TryGetProperty("thread_name", out _));     // first attempt: forum mode...
        Assert.False(host.EntryBody(1).TryGetProperty("thread_name", out _));    // second attempt: swapped to embed — exactly one swap, in the right direction

        var all = host.Capture.Snapshot();
        Assert.Single(all, e => e.Level == LogLevel.Warning && e.Message.Contains("resending post"));   // the target's one-shot swap warning...
        Assert.Null(host.Capture.Find(LogLevel.Information, "retrying in"));      // ...and NO worker-level retry wait — the 400 was absorbed inside SendAsync

        Assert.Single(host.Store.MarkDeliveredCalls);   // confirmed EXACTLY once — delivered on the FIRST worker attempt
        var confirm = host.Store.MarkDeliveredCalls[0]; // ! safe: Single above guarantees the entry
        Assert.Equal(InstanceId, confirm.InstanceId);
        Assert.Equal(FeedUrl, confirm.FeedUrl);
        Assert.Equal(ItemId, confirm.ItemId);
        Assert.Null(confirm.NewFeedState);
    }

    [Fact]
    public async Task Retry_SequenceOf429sThenDelivered_ExactSendCountAndSingleConfirmation()
    {
        // base 1 ms pinned at jitter 1.0 ⇒ the jittered candidate (1 ms) is far below the 50 ms body hint — each wait is EXACTLY the retry_after_ms floor.
        using var host = RetryHost.Create("embed", backoffBase: TimeSpan.FromMilliseconds(1), jitterSource: () => 1.0);

        // Calls 1 and 2 → 429 with body retry_after_ms: 50; call 3 → 204 (sequenced via the callback counter).
        host.Sequence(n => n <= 2
            ? Response.Create().WithStatusCode(429).WithHeader("Content-Type", "application/json").WithBody("""{"retry_after_ms":50}""").ResponseMessage
            : Response.Create().WithStatusCode(204).ResponseMessage);

        using var cts = new CancellationTokenSource();
        await host.Worker.StartAsync(cts.Token);
        await host.Queue.EnqueueAsync(MakePost(), cts.Token);

        await WaitUntilAsync(() => host.Store.MarkDeliveredCalls.Count == 1, "the post to be confirmed");
        await host.Worker.StopAsync(CancellationToken.None);

        Assert.Equal(3, host.Calls);   // EXACTLY three sends — two retries implied by the sequence, no more (decision 4)

        var firstGap = host.BetweenCalls(1, 2);
        Assert.True(firstGap >= TimeSpan.FromMilliseconds(50) - ClockEpsilon, $"waited only {firstGap} after the first 429 — the retry_after_ms floor (50 ms) is absolute: never less");
        var secondGap = host.BetweenCalls(2, 3);
        Assert.True(secondGap >= TimeSpan.FromMilliseconds(50) - ClockEpsilon, $"waited only {secondGap} after the second 429 — the retry_after_ms floor (50 ms) is absolute: never less");

        Assert.Single(host.Store.MarkDeliveredCalls);   // confirmed EXACTLY once across both retries...
        var confirm = host.Store.MarkDeliveredCalls[0]; // ! safe: Single above guarantees the entry
        Assert.Equal(InstanceId, confirm.InstanceId);
        Assert.Equal(FeedUrl, confirm.FeedUrl);
        Assert.Equal(ItemId, confirm.ItemId);
        Assert.Null(confirm.NewFeedState);

        Assert.Null(host.Capture.Find(LogLevel.Error, "Dead letter"));   // delivered ⇒ no dead-letter decision
    }

    private static Post MakePost() => new()
    {
        Id = Guid.NewGuid().ToString(),
        ItemId = ItemId,
        Title = "Retry post",
        Link = "https://example.com/item",
        PublishedAt = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero),   // always UTC
        SourceUrl = FeedUrl,
    };

    /// <summary>Polls a condition every 5 ms until it holds or the (generous) timeout passes — worker work happens on its own task.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 2000 && !condition(); i++)   // up to ~10 s — far beyond every pinned wait in these tests
            await Task.Delay(5);

        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    /// <summary>One WireMock server + the REAL named "discord" client (short timeout) + the REAL DiscordNotificationTarget (config built in-test,
    /// webhook = server URL) + the REAL BoundedSendQueue (small capacity) + the REAL DedupEngine over the RecordingStateStore fake + the REAL
    /// SendWorker with injected small backoff constants and deterministic jitter where a scenario needs exact timing (decision 2).</summary>
    private sealed class RetryHost : IDisposable
    {
        private readonly ServiceProvider _provider;

        public RetryHost(WireMockServer server, BoundedSendQueue queue, SendWorker worker, RecordingStateStore store, LogCapture capture, ServiceProvider provider)
        {
            Server = server;
            Queue = queue;
            Worker = worker;   // every test stops the worker before it ends — Dispose then only releases the server and the scope factory (same pattern as SendWorkerTests)
            Store = store;
            Capture = capture;
            _provider = provider;   // BuildServiceProvider returns a ServiceProvider in practice — kept concrete so Dispose can release it
        }

        public WireMockServer Server { get; }
        public BoundedSendQueue Queue { get; }
        public SendWorker Worker { get; }
        public RecordingStateStore Store { get; }
        public LogCapture Capture { get; }

        /// <summary>Request-arrival timestamps (UTC) in order — recorded from the sequenced-answer callback.</summary>
        public ConcurrentQueue<DateTime> Stamps { get; } = new();

        /// <summary>The exact send count — via the WireMock callback counter (decision 4: what "posts are never duplicated on the retry paths" means operationally).</summary>
        public int Calls => Stamps.Count;

        /// <summary>Sequenced answers (T-020 pattern — WireMock 2.x has no ordered stubs): the callback counts calls with Interlocked, timestamps every
        /// request arrival, and picks the answer for call n (1-based) from <paramref name="respond"/>.</summary>
        public void Sequence(Func<int, IResponseMessage> respond)
        {
            var calls = 0;

            Server.Given(Request.Create())
                  .RespondWith(Response.Create().WithCallback(_ =>
                  {
                      var n = Interlocked.Increment(ref calls);   // single reader per worker — Interlocked to be safe (plan risk table)
                      Stamps.Enqueue(DateTime.UtcNow);            // arrival timestamp for the floor-based timing assertions
                      return respond(n);
                  }));
        }

        /// <summary>Elapsed time between two request arrivals (1-based call numbers).</summary>
        public TimeSpan BetweenCalls(int firstCall, int secondCall)
        {
            var stamps = Stamps.ToArray();   // one request in flight at a time — order preserved; snapshot for the assertion
            return stamps[secondCall - 1] - stamps[firstCall - 1];
        }

        /// <summary>Total elapsed from the first to the last request arrival.</summary>
        public TimeSpan FirstToLast()
        {
            var stamps = Stamps.ToArray();   // one request in flight at a time — order preserved; snapshot for the assertion
            return stamps[^1] - stamps[0];
        }

        /// <summary>The JSON body of the n-th logged request (0-based) — for asserting the swap direction.</summary>
        public JsonElement EntryBody(int index) => JsonSerializer.Deserialize<JsonElement>(Server.LogEntries[index].RequestMessage!.Body!);   // ! safe: a logged request always carries its message and body (WireMock log contract, same as DiscordTargetSendTests)

        public static RetryHost Create(string mode, TimeSpan? backoffBase = null, TimeSpan? backoffCap = null, Func<double>? jitterSource = null)
        {
            var server = WireMockServer.Start();   // dynamic free port — no fixed binding (CI-safe)

            var capture = new LogCapture();
            var store = new RecordingStateStore();
            var queue = new BoundedSendQueue(capacity: 4);   // small capacity — tests never use the default 100

            var services = new ServiceCollection();
            services.AddHttpClient("").ConfigurePrimaryHttpMessageHandler(() => new FailFastHandler());   // unnamed client → loud failure if touched (named-client proof, T-020 pattern)
            services.AddHttpClient(DiscordNotificationTarget.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5));   // short timeout — test-only
            services.AddScoped<LogEnricher>();   // scoped LogEnricher (T-004): the worker creates one scope per post — this minimal container supplies it (same pattern as SendWorkerTests)

            var provider = services.BuildServiceProvider();

            var target = new DiscordNotificationTarget(
                provider.GetRequiredService<IHttpClientFactory>(),
                new DiscordTargetConfig { WebhookUrl = server.Url, Mode = mode },   // webhook = the sequenced server — the full real path
                capture.For<DiscordNotificationTarget>());

            var dedup = new DedupEngine(store, capture.For<DedupEngine>());   // real engine over the recording store — confirm arguments are assertable

            var worker = new SendWorker(
                InstanceId, queue, target, dedup,
                provider.GetRequiredService<IServiceScopeFactory>(), capture.For<SendWorker>(),
                backoffBase: backoffBase, backoffCap: backoffCap, jitterSource: jitterSource);   // test-only injectables — deterministic timing (T-021 decision 1)

            return new RetryHost(server, queue, worker, store, capture, provider);
        }

        public void Dispose()
        {
            Server.Dispose();
            _provider.Dispose();   // the worker is already stopped by the test — disposing its scope factory is safe then (same pattern as SendWorkerTests)
        }
    }

    /// <summary>Thrown instead of sending when the default (unnamed) client is used — proof that the target uses the named "discord" client.</summary>
    private sealed class FailFastHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The default (unnamed) HTTP client was used — the target must POST through the named \"discord\" client.");
    }
}
