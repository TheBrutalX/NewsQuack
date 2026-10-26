using System.Diagnostics;              // Stopwatch — bounded promptness assertions on cancellation (issue #56 / constraint 6)
using NewsQuack.App.Configuration;      // DiscordTargetConfig / InstanceConfig / FeedConfig / NewsQuackConfig / HttpSectionConfig — the in-test config the registry is built from
using NewsQuack.App.Discord;            // DiscordNotificationTarget + WebhookPacer/WebhookPacerRegistry — the REAL target and pacing pieces under test
using NewsQuack.App.Http;               // AddNewsQuackHttpClients — the REAL production HTTP wiring (the pacing handler sits in the "discord" pipeline)
using NewsQuack.App.Logging;            // LogEnricher — registered Scoped, resolved per post scope by the worker (T-021 decision 5)
using NewsQuack.App.Pipeline;           // BoundedSendQueue / DedupEngine — the real pieces the worker consumes
using NewsQuack.App.Sending;            // SendWorker / DeadLetterSink / DeadLetterNotifier — the REAL per-instance delivery path
using NewsQuack.Core;                   // Post — the frozen contract
using Microsoft.Extensions.DependencyInjection;   // ServiceCollection — minimal container: production HTTP wiring + scoped LogEnricher
using WireMock;                        // IResponseMessage — the sequenced-answer callback's return type (WireMock 2.x)
using WireMock.RequestBuilders;        // Request — stubbing the webhook server
using WireMock.ResponseBuilders;       // Response — sequenced answers inside the WithCallback counter (T-020 pattern)
using WireMock.Server;                 // WireMockServer — dynamic port, CI-safe
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-048: end-to-end pacing regression tests through the REAL <see cref="DiscordNotificationTarget"/> pointed at a self-hosted WireMock serving SEQUENCED answers, drained by the REAL per-instance
/// <see cref="SendWorker"/>, with the REAL production HTTP wiring in front (AddNewsQuackHttpClients + AddWebhookPacing — the pacing handler is part of the "discord" client's pipeline exactly as in
/// Program.cs). Pins issue #56 as a whole-system property: no two requests to the same webhook URL start closer than the configured minimum interval (default 3 s ⇒ at most 21 requests per closed 60 s window, exactly 20/min sustained); pacing counts REQUESTS,
/// not posts (the fallback swap's second POST is paced too); pacing state is shared by URL across instances (strictest interval wins); pacing COMPOSES with retry/backoff/Retry-After (never replaces it);
/// and cancellation during a pacing wait surfaces promptly without blocking the shutdown drain. Nothing in the path is scripted: real target + real worker + real queue + real dedup engine; only the HTTP
/// answers are sequenced via a WithCallback counter (WireMock 2.x has no ordered stubs). Timing assertions use the PACER'S OWN recorded reservation stamps (the ground truth for request STARTS — server-side
/// arrival stamps can lag a start by variable server processing time) with the established 25 ms clock epsilon (T-044 pattern) — never wall-clock absolutes.
/// </summary>
public class WebhookPacingTests
{
    private const string FeedUrl = "https://example.com/feed.xml";

    /// <summary>The channel-type 400 body — nested string-code signal, same constant as DiscordTargetSendTests / SendWorkerRetryTests (T-020/T-044).</summary>
    private const string ChannelTypeCodeStringBody = """{"embeds":[],"non_json_objects":[],"errors":{"thread_name":{"code":"50_071"}}}""";

    /// <summary>Small tolerance for clock granularity on the floor-based timing assertions — the property under test is "never start CLOSER", so the
    /// epsilon can only shave milliseconds off a pinned floor, never seconds (same pattern as SendWorkerRetryTests / GracefulShutdownTests).</summary>
    private static readonly TimeSpan ClockEpsilon = TimeSpan.FromMilliseconds(25);

    [Fact]
    public async Task Pacing_TwoConsecutiveSuccessfulSends_SecondRequestStartsAtLeastIntervalAfterFirst()
    {
        // Interval pinned to the minimum allowed (1 s) — two fast 204s must still be spaced ≥ 1 s between request STARTS (constraint 1: measure between starts).
        using var host = PacingHost.Create(1, minSendIntervalSeconds: 1);

        host.Sequence(_ => Response.Create().WithStatusCode(204).ResponseMessage);

        using var cts = new CancellationTokenSource();
        await host.Worker.StartAsync(cts.Token);
        await host.Queue.EnqueueAsync(MakePost("item-1"), cts.Token);
        await host.Queue.EnqueueAsync(MakePost("item-2"), cts.Token);

        await WaitUntilAsync(() => host.Store.MarkDeliveredCalls.Count == 2, "both posts to be confirmed");
        await host.Worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, host.Calls);   // exactly two requests — one per post; pacing counts requests (constraint 1)

        var starts = PacerStarts(host);   // the pacer's own reservation stamps — the ground truth for request STARTS (server arrival stamps can lag by variable server processing time)
        Assert.Equal(2, starts.Count);

        var gap = starts[1] - starts[0];
        Assert.True(gap >= TimeSpan.FromSeconds(1) - ClockEpsilon, $"second request started only {gap} after the first — no two requests to the same webhook URL may start closer than the 1 s interval");
        Assert.True(gap < TimeSpan.FromSeconds(3), $"second request started {gap} after the first — far beyond one pace floor: nothing stacks on top of pacing");

        Assert.Equal(2, host.Store.MarkDeliveredCalls.Count);   // both delivered — pacing delays requests, it never drops them
    }

    [Fact]
    public async Task Pacing_RetryAfterLargerThanPace_WaitsRetryAfterNotPace()
    {
        // Pace floor 1 s; the 429's Retry-After (3 s) is well above it ⇒ the total wait must be ≈ Retry-After — pacing COMPOSES with the retry policy (max), it never stacks on top of it (constraint 5).
        using var host = PacingHost.Create(1, minSendIntervalSeconds: 1);

        // Call 1 → 429 with a Retry-After header of 3 s; call 2 → 204 (sequenced via the callback counter).
        host.Sequence(n => n == 1
            ? Response.Create().WithStatusCode(429).WithHeader("Retry-After", "3").WithBody("{}").ResponseMessage
            : Response.Create().WithStatusCode(204).ResponseMessage);

        using var cts = new CancellationTokenSource();
        await host.Worker.StartAsync(cts.Token);
        await host.Queue.EnqueueAsync(MakePost("item-1"), cts.Token);

        await WaitUntilAsync(() => host.Store.MarkDeliveredCalls.Count == 1, "the post to be confirmed");
        await host.Worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, host.Calls);   // the 429 retried once, then delivered — no extra or parallel sends

        var starts = PacerStarts(host);   // reservation stamps between the two attempts — the total wait as measured at request STARTS
        Assert.Equal(2, starts.Count);

        var gap = starts[1] - starts[0];
        Assert.True(gap >= TimeSpan.FromSeconds(3) - ClockEpsilon, $"waited only {gap} between attempts — the Retry-After floor (3 s) is absolute: never less");
        Assert.True(gap < TimeSpan.FromSeconds(4.5), $"waited {gap} between attempts — far beyond the 3 s Retry-After: a long Retry-After must still dominate, pacing may not add itself on top");

        Assert.Single(host.Store.MarkDeliveredCalls);   // delivered on retry — confirmed exactly once
    }

    [Fact]
    public async Task Pacing_FallbackSwapProducesTwoPostsInOneAttempt_BothRequestsPaced()
    {
        // One SendAsync sends TWO requests (the one-shot channel-type swap) — pacing counts REQUESTS, not posts (constraint 1): the second POST of the swap must also wait for the pace floor. A future change that
        // paces only posts (or exempts the swap) shrinks this gap to milliseconds and fails here.
        using var host = PacingHost.Create(1, minSendIntervalSeconds: 1);

        // Call 1 → channel-type 400 (50_071 string-code signal); call 2 → 204 — BOTH inside the single worker attempt (the target's one-shot swap).
        host.Sequence(n => n == 1
            ? Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody(ChannelTypeCodeStringBody).ResponseMessage
            : Response.Create().WithStatusCode(204).ResponseMessage);

        using var cts = new CancellationTokenSource();
        await host.Worker.StartAsync(cts.Token);
        await host.Queue.EnqueueAsync(MakePost("item-1"), cts.Token);

        await WaitUntilAsync(() => host.Store.MarkDeliveredCalls.Count == 1, "the post to be confirmed");
        await host.Worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, host.Calls);   // exactly two requests — the swap happened once inside ONE SendAsync; a worker-level retry would make it ≥ 3 (T-044 decision 4)

        var starts = PacerStarts(host);   // both POSTs of the one attempt must appear as paced starts — requests are counted, never posts
        Assert.Equal(2, starts.Count);

        var gap = starts[1] - starts[0];
        Assert.True(gap >= TimeSpan.FromSeconds(1) - ClockEpsilon, $"the swap's second POST started only {gap} after the first — BOTH requests of one attempt traverse the pacer (requests are counted, never posts)");
        Assert.True(gap < TimeSpan.FromSeconds(3), $"the swap's second POST started {gap} after the first — far beyond one pace floor: nothing stacks on top of pacing");

        Assert.Single(host.Store.MarkDeliveredCalls);   // delivered on the swapped request — confirmed exactly once
    }

    [Fact]
    public async Task Pacing_TwoInstancesSameWebhookUrl_CombinedTrafficWithinTwentyPerMinute()
    {
        // Two instances/queues against ONE webhook URL, MinSendIntervalSeconds omitted ⇒ the default 3 s floor applies (T-051; T-048 constraint 2). The pacer is shared by URL, NOT per instance (constraint 3): combined traffic
        // respects one budget — N requests span ≥ (N−1) × interval. A per-INSTANCE pacer would let both instances' first requests start at once and shrink the span below that floor.
        using var host = PacingHost.Create(2);   // both instances point at the same server URL with the default interval

        host.Sequence(_ => Response.Create().WithStatusCode(204).ResponseMessage);

        using var cts = new CancellationTokenSource();
        await host.Workers[0].StartAsync(cts.Token);
        await host.Workers[1].StartAsync(cts.Token);
        await host.Queues[0].EnqueueAsync(MakePost("a-1"), cts.Token);   // instance A: two posts...
        await host.Queues[0].EnqueueAsync(MakePost("a-2"), cts.Token);
        await host.Queues[1].EnqueueAsync(MakePost("b-1"), cts.Token);   // ...instance B: one post — 3 requests combined on ONE webhook

        await WaitUntilAsync(() => host.Store.MarkDeliveredCalls.Count == 3, "all three posts to be confirmed");
        await host.Workers[0].StopAsync(CancellationToken.None);
        await host.Workers[1].StopAsync(CancellationToken.None);

        Assert.Equal(3, host.Calls);   // exactly three requests combined — nothing lost or duplicated across instances

        var starts = PacerStarts(host);   // the ONE shared pacer's reservation stamps for all 3 combined requests (constraint 3: state is per URL, not per instance)
        Assert.Equal(3, starts.Count);

        var span = starts[^1] - starts[0];
        Assert.True(span >= TimeSpan.FromSeconds(6) - ClockEpsilon, $"the 3 combined requests spanned only {span} — with a shared 3 s per-URL floor they must span ≥ (3−1) × 3 s (≤ 20 req/min sustained on that webhook); a per-INSTANCE pacer would let both first requests start at once and shrink the span");
        Assert.True(span < TimeSpan.FromSeconds(12), $"the 3 combined requests spanned {span} — far beyond the two pace floors: no runaway pacing");

        Assert.Equal(3, host.Store.MarkDeliveredCalls.Count);   // all delivered — shared pacing delays requests, it never drops them
    }

    [Fact]
    public async Task Pacing_CancellationDuringWait_ReturnsPromptlyAndDoesNotBlockDrain()
    {
        // Default interval (3 s, T-051). Post 1 sends immediately (the first request is never delayed) and confirms; post 2 then sits in the pacing wait for the floor. Cancelling while that wait is active must
        // surface OCE promptly — a pace floor must never block the shutdown drain (constraint 6 / T-031's bounded window).
        using var host = PacingHost.Create(1);

        host.Sequence(_ => Response.Create().WithStatusCode(204).ResponseMessage);

        using var cts = new CancellationTokenSource();
        await host.Worker.StartAsync(cts.Token);
        await host.Queue.EnqueueAsync(MakePost("item-1"), cts.Token);
        await host.Queue.EnqueueAsync(MakePost("item-2"), cts.Token);

        await WaitUntilAsync(() => host.Store.MarkDeliveredCalls.Count == 1, "the first post to be confirmed");

        // Deterministic: wait until the second post is PROVABLY inside the pacing wait (internal hook) before cancelling — no timing race between dequeue and the pace floor.
        var pacer = host.Registry.For(host.WebhookUrl);   // the SAME pacer instance the "discord" client's handler resolves for this URL
        await WaitUntilAsync(() => pacer.ActiveWaits == 1, "the second post to enter the pacing wait");

        var stoppedAt = Stopwatch.StartNew();
        await host.Worker.StopAsync(CancellationToken.None);   // cancels the stopping token — the in-flight pace wait must surface OCE promptly
        stoppedAt.Stop();

        Assert.True(stoppedAt.Elapsed < TimeSpan.FromSeconds(1.5), $"stop took {stoppedAt.Elapsed} — cancellation during the pacing wait must surface promptly (up to ~3 s of pace floor remain; a blocked drain would have waited it out)");
        Assert.Single(host.Store.MarkDeliveredCalls);   // post 2 dropped unconfirmed — re-delivered on the next start (at-least-once, T-021 decision 2)
    }

    [Fact]
    public async Task Pacer_FirstRequestToUrl_IsNeverDelayed()
    {
        var pacer = new WebhookPacer(TimeSpan.FromSeconds(2));   // a full 2 s floor that the first request must NOT wait out (pinned semantics, constraint 1)

        var startedAt = Stopwatch.StartNew();
        await pacer.WaitUntilAllowedAsync();

        Assert.True(startedAt.Elapsed < TimeSpan.FromMilliseconds(500), $"first request took {startedAt.Elapsed} — the first request to a URL is never delayed");
    }

    [Fact]
    public async Task Pacer_ConcurrentCallers_EveryStartSpacedByAtLeastInterval()
    {
        // Four concurrent callers against one pacer (the shared-URL scenario): the gate must serialize start-slot reservation so NO two starts land closer than the interval — even when all four arrive at once.
        var pacer = new WebhookPacer(TimeSpan.FromMilliseconds(150));

        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() => pacer.WaitUntilAllowedAsync())).ToArray();
        await Task.WhenAll(tasks);

        var starts = pacer.StartStampsUtc;   // the pacer's own reservation stamps — in reservation order (the gate serializes them)
        Assert.Equal(4, starts.Count);

        for (var i = 1; i < starts.Count; i++)
        {
            var gap = starts[i] - starts[i - 1];
            Assert.True(gap >= TimeSpan.FromMilliseconds(150) - ClockEpsilon, $"concurrent starts #{i} and #{i + 1} are only {gap} apart — the shared pacer must space every start by ≥ the interval (a lost reservation under concurrency would break pacing)");
        }

        Assert.True(starts[^1] - starts[0] >= TimeSpan.FromMilliseconds(450) - ClockEpsilon, $"4 concurrent requests spanned only {starts[^1] - starts[0]} — they must span ≥ 3 × 150 ms");
    }

    [Fact]
    public void Registry_TwoInstancesSameUrlDifferentIntervals_StrictestIntervalWins()
    {
        var config = new NewsQuackConfig { Instances = [ Instance("a", 5), Instance("b", 2) ] };   // same webhook URL, intervals 5 s and 2 s

        var registry = new WebhookPacerRegistry(config);

        Assert.Equal(TimeSpan.FromSeconds(2), registry.For(ConfigTestHelper.WebhookUrl).Interval);   // the smallest (strictest) interval applies to the shared URL (constraint 3)
    }

    [Fact]
    public void Registry_OmittedMinSendIntervalSeconds_UsesDefaultThreeSeconds()
    {
        var config = new NewsQuackConfig { Instances = [ Instance("a", null) ] };   // key omitted — the schema default must apply (T-051; T-048 constraint 2)

        var registry = new WebhookPacerRegistry(config);

        Assert.Equal(TimeSpan.FromSeconds(3), registry.For(ConfigTestHelper.WebhookUrl).Interval);   // literal, not constant-relative: pins the pre-registered path against a future silent move of the default
    }

    [Fact]
    public void Registry_SameWebhookUrl_ReturnsTheSamePacerInstance()
    {
        var config = new NewsQuackConfig { Instances = [ Instance("a", 2), Instance("b", 3) ] };   // two instances, one URL — the per-URL (NOT per-instance) sharing property

        var registry = new WebhookPacerRegistry(config);

        Assert.Same(registry.For(ConfigTestHelper.WebhookUrl), registry.For(ConfigTestHelper.WebhookUrl));
    }

    [Fact]
    public void Registry_UnknownUrl_GetsSharedDefaultIntervalPacer()
    {
        // URLs not in the config (e.g. the operational dead-letter webhook — an env var, not a schema key) are still paced: an on-demand pacer with the default interval, the same instance across calls, so EVERY
        // request through the "discord" client is paced (constraint 1).
        var registry = new WebhookPacerRegistry(new NewsQuackConfig { Instances = [ Instance("a", null) ] });

        const string unknownUrl = "https://discord.com/api/webhooks/42/DeadLetterToken";

        Assert.Equal(TimeSpan.FromSeconds(3), registry.For(unknownUrl).Interval);   // literal, not constant-relative: pins the on-demand fallback against a future silent move of the default (T-051)
        Assert.Same(registry.For(unknownUrl), registry.For(unknownUrl));   // shared state for unknown URLs too — no unpaced path through the "discord" client
    }

    /// <summary>The pacer's own reservation stamps for this host's webhook URL — the ground truth for "request STARTS" assertions (the SAME pacer instance the
    /// "discord" client's handler consults). Server-side arrival stamps are kept only for exact request COUNTS: their timing can lag a request's actual start by
    /// variable server processing time, which would make floor assertions flaky.</summary>
    private static List<DateTime> PacerStarts(PacingHost host) => host.Registry.For(host.WebhookUrl).StartStampsUtc;

    private static Post MakePost(string itemId) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ItemId = itemId,   // distinct per post — the dedup key; each post is delivered exactly once
        Title = "Pacing post",
        Link = "https://example.com/item",
        PublishedAt = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero),   // always UTC
        SourceUrl = FeedUrl,
    };

    private static InstanceConfig Instance(string id, int? minSendIntervalSeconds) => new()
    {
        Id = id,
        CheckIntervalMinutes = 15,   // not consumed by the registry — present so the config object is total (no boot validation in unit tests)
        Feeds = [new FeedConfig { Url = FeedUrl }],   // same: not consumed (no fetcher here)
        Discord = new DiscordTargetConfig
        {
            WebhookUrl = ConfigTestHelper.WebhookUrl,   // every instance points at the same webhook — the sharing property under test
            MinSendIntervalSeconds = minSendIntervalSeconds,
        },
    };

    /// <summary>Polls a condition every 5 ms until it holds or the (generous) timeout passes — worker work happens on its own task (T-044 pattern).</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 2000 && !condition(); i++)   // up to ~10 s — far beyond every pinned wait in these tests
            await Task.Delay(5);

        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    /// <summary>One WireMock server + the REAL production HTTP wiring (AddNewsQuackHttpClients — the pacing handler sits in the "discord" pipeline) + AddWebhookPacing built from an in-test config whose webhook IS the
    /// server URL, plus one REAL DiscordNotificationTarget + BoundedSendQueue + DedupEngine (over the RecordingStateStore fake) + SendWorker per configured instance. The pacer registry is resolved from DI — the exact
    /// same instance the handler consults, so tests can observe pacing state (ActiveWaits) deterministically.</summary>
    private sealed class PacingHost : IDisposable
    {
        private readonly ServiceProvider _provider;

        public PacingHost(WireMockServer server, WebhookPacerRegistry registry, LogCapture capture, RecordingStateStore store, ServiceProvider provider)
        {
            Server = server;
            Registry = registry;
            Capture = capture;
            Store = store;
            _provider = provider;   // BuildServiceProvider returns a ServiceProvider in practice — kept concrete so Dispose can release it (T-044 pattern)
        }

        public WireMockServer Server { get; }

        /// <summary>The server's base URL — the webhook every instance in this host points at (the registry normalizes it to the same key the request URIs use).</summary>
        public string WebhookUrl => Server.Url!;   // ! safe: WireMockServer.Start() always binds a listener and exposes its URL

        public WebhookPacerRegistry Registry { get; }
        public LogCapture Capture { get; }
        public RecordingStateStore Store { get; }

        /// <summary>One queue + one worker per configured instance (single-reader each, T-021/T-024).</summary>
        public List<BoundedSendQueue> Queues { get; } = new();
        public List<SendWorker> Workers { get; } = new();

        /// <summary>Convenience for the single-instance hosts: this host's queue / worker (every single-instance test creates exactly one instance before touching these).</summary>
        public BoundedSendQueue Queue => Queues[0];
        public SendWorker Worker => Workers[0];

        /// <summary>The exact request count — via the WireMock callback counter (T-044 decision 4). Timing assertions use the pacer's own reservation stamps
        /// (<see cref="WebhookPacer.StartStampsUtc"/>) instead: arrival timestamps can lag a request's actual start by variable server processing time.</summary>
        public int Calls => Volatile.Read(ref _calls);

        private int _calls;   // incremented from the sequenced-answer callback (server thread) — volatile read for the assertions

        /// <summary>Sequenced answers (T-020 pattern — WireMock 2.x has no ordered stubs): the callback counts calls and picks the answer for call n
        /// (1-based) from <paramref name="respond"/>.</summary>
        public void Sequence(Func<int, IResponseMessage> respond)
        {
            var sequenceCalls = 0;

            Server.Given(Request.Create())
                  .RespondWith(Response.Create().WithCallback(_ =>
                  {
                      Interlocked.Increment(ref _calls);   // host-wide request count — two workers may hit this server, Interlocked to be safe (T-044 pattern)
                      return respond(Interlocked.Increment(ref sequenceCalls));   // per-sequence counter for answer selection (1-based)
                  }));
        }

        public static PacingHost Create(int instanceCount, int? minSendIntervalSeconds = null)
        {
            if (instanceCount < 1)
                throw new ArgumentOutOfRangeException(nameof(instanceCount), instanceCount, "At least one instance is required.");

            var server = WireMockServer.Start();   // dynamic free port — no fixed binding (CI-safe)

            // The in-test config: every instance points at the SAME server URL (the sharing property under test); MinSendIntervalSeconds omitted ⇒ default 3 s (T-051).
            var instances = new List<InstanceConfig>();
            for (var i = 0; i < instanceCount; i++)
                instances.Add(new InstanceConfig
                {
                    Id = $"inst-{i}",
                    CheckIntervalMinutes = 15,   // not consumed here — no scheduler in this host; the value just keeps the object total
                    Feeds = [new FeedConfig { Url = FeedUrl }],   // same: not consumed (no fetcher), present for a total config
                    Discord = new DiscordTargetConfig
                    {
                        WebhookUrl = server.Url,   // webhook = the sequenced server — pacing state is keyed by its normalized form (registry normalizes config keys and request URIs identically, constraint 3)
                        MinSendIntervalSeconds = minSendIntervalSeconds,   // omitted ⇒ default 3 s (T-051; T-048 constraint 2)
                    },
                });

            var config = new NewsQuackConfig { Instances = instances };

            var capture = new LogCapture();
            var store = new RecordingStateStore();

            var services = new ServiceCollection();
            services.AddHttpClient("").ConfigurePrimaryHttpMessageHandler(() => new FailFastHandler());   // unnamed client → loud failure if touched (named-client proof, T-020 pattern)
            services.AddNewsQuackHttpClients(new HttpSectionConfig { DiscordTimeoutSeconds = 5 });   // the REAL production wiring — the pacing handler sits in the "discord" pipeline (T-048)
            services.AddWebhookPacing(config);   // the shared registry built from THIS config — strictest interval per URL, default when omitted (constraint 3)
            services.AddScoped<LogEnricher>();   // scoped LogEnricher (T-004): the worker creates one scope per post — this minimal container supplies it

            var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IHttpClientFactory>();
            var registry = provider.GetRequiredService<WebhookPacerRegistry>();   // the SAME singleton the "discord" client's pacing handler resolves

            var host = new PacingHost(server, registry, capture, store, provider);

            for (var i = 0; i < instanceCount; i++)
            {
                var id = instances[i].Id!;      // ! safe: set in the loop above
                var discord = instances[i].Discord!;   // ! safe: set in the loop above

                var queue = new BoundedSendQueue(capacity: 4);   // small capacity — tests never use the default 100 (T-024 pattern)
                var target = new DiscordNotificationTarget(factory, discord, capture.For<DiscordNotificationTarget>());   // the REAL target — both POSTs of a swap traverse the paced "discord" client
                var dedup = new DedupEngine(store, capture.For<DedupEngine>());   // real engine over the recording store — confirm arguments are assertable
                var sink = new DeadLetterSink(id, store,
                    new DeadLetterNotifier(null, factory, capture.For<DeadLetterNotifier>()),   // disabled notifier (null URL) — dead letters persist + log through the production path (T-022)
                    capture.For<DeadLetterSink>());

                host.Queues.Add(queue);
                host.Workers.Add(new SendWorker(
                    id, queue, target, dedup,
                    provider.GetRequiredService<IServiceScopeFactory>(), sink, capture.For<SendWorker>(),
                    backoffBase: TimeSpan.FromMilliseconds(5), jitterSource: () => 1.0));   // tiny pinned backoff — any (absent) retry stays fast; every floor under test is ≥ 1 s and dominates it
            }

            return host;
        }

        public void Dispose()
        {
            Server.Dispose();
            _provider.Dispose();   // the workers are already stopped by the test — disposing their scope factory is safe then (T-044 pattern)
        }
    }

    /// <summary>Thrown instead of sending when the default (unnamed) client is used — proof that the target uses the named "discord" client (T-020 pattern).</summary>
    private sealed class FailFastHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The default (unnamed) HTTP client was used — the target must POST through the named \"discord\" client.");
    }
}
