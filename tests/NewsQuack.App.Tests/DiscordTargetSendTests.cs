using System.Net;
using System.Text;
using System.Text.Json;
using NewsQuack.App.Configuration;
using NewsQuack.App.Discord;
using NewsQuack.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-020 HTTP mapping tests: the target is pointed at a self-hosted WireMock on a dynamic port (CI-safe). The named "discord"
/// client is proven by failing fast if the default (unnamed) client is ever used. Sequential answers use a WithCallback counter —
/// WireMock 2.x has no ordered stubs, and ThenRespondWith serves the "then" response from call 1.</summary>
public class DiscordTargetSendTests
{
    private const string ChannelTypeMessageBody = """{"embeds":null,"non_json_objects":null,"message":"Channel type not supported"}""";
    private const string ChannelTypeCodeStringBody = """{"embeds":[],"non_json_objects":[],"errors":{"thread_name":{"code":"50_071"}}}""";
    private const string ChannelTypeBothSignalsBody = """{"message":"Channel type not supported","code":50071}""";

    [Fact]
    public async Task Send_204_ReturnsDelivered()
    {
        using var host = TestHost.Create();

        host.Server.Given(Request.Create())
                 .RespondWith(Response.Create().WithStatusCode(204));

        var result = await host.Target.SendAsync(MakePost());

        Assert.Equal(TargetOutcome.Delivered, result.Outcome);
        Assert.Null(result.RetryAfter);
        Assert.Single(host.Entries);   // exactly one POST — no retry logic in the target
    }

    [Fact]
    public async Task Send_429WithRetryAfterHeader_ReturnsRetryableWithThatDuration()
    {
        using var host = TestHost.Create();

        host.Server.Given(Request.Create())
                 .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "12").WithBody("{}"));

        var result = await host.Target.SendAsync(MakePost());

        Assert.Equal(TargetOutcome.Retryable, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(12), result.RetryAfter);   // header wins — seconds
        Assert.Equal("Discord rate limit", result.Error);
    }

    [Fact]
    public async Task Send_429BodyRetryAfterMs_ReturnsRetryableInMilliseconds()
    {
        using var host = TestHost.Create();

        host.Server.Given(Request.Create())
                 .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Content-Type", "application/json").WithBody("""{"retry_after_ms":2500}"""));

        var result = await host.Target.SendAsync(MakePost());

        Assert.Equal(TargetOutcome.Retryable, result.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), result.RetryAfter);   // no header → body, milliseconds honored as such
    }

    [Fact]
    public async Task Send_429BodyRetryAfterSeconds_ReturnsRetryableInSeconds()
    {
        using var host = TestHost.Create();

        host.Server.Given(Request.Create())
                 .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Content-Type", "application/json").WithBody("""{"retry_after":3}"""));

        var result = await host.Target.SendAsync(MakePost());

        Assert.Equal(TargetOutcome.Retryable, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(3), result.RetryAfter);
    }

    [Fact]
    public async Task DiscordTarget_WebhookResponseBodyLargerThanCap_RetryAfterStillParsedFromPrefix()
    {
        using var host = TestHost.Create();

        // 429 whose body starts with a COMPLETE retry_after_ms object, padded with whitespace past the 64 KiB cap. JSON allows trailing whitespace after the
        // root value, so the truncated PREFIX is still valid JSON and retry-after parses from it (a dangling comma would not survive strict parsing — the plan's
        // "parses whatever key fits in the prefix (or none)"). No Retry-After header — the body is the only source.
        const string head = """{"retry_after_ms":1500}""";
        const int cap = 64 * 1024;   // DiscordNotificationTarget.MaxWebhookResponseBytes (private by design — constants, no config key)
        var body = head + new string(' ', cap - head.Length + 128);   // total > cap → truncated at the cap…
        Assert.True(Encoding.UTF8.GetByteCount(body) > cap);

        host.Server.Given(Request.Create())
              .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Content-Type", "application/json").WithBody(Encoding.UTF8.GetBytes(body)));

        var result = await host.Target.SendAsync(MakePost());   // no exception escapes — a truncated body is not a failure mode

        Assert.Equal(TargetOutcome.Retryable, result.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), result.RetryAfter);   // …and retry-after is still parsed from the truncated prefix (1.5 s visible at target level)
        var warning = host.Logger.Messages.FirstOrDefault(m => m.StartsWith("Warning") && m.Contains("exceeds"));
        Assert.NotNull(warning);                                             // the over-cap Warning must have been logged…
        Assert.Contains(WebhookMasker.Mask(host.Server.Url)!, warning!);     // ! safe ×2: Mask only returns null for null input (server.Url is a live URL — WireMock contract), and the Assert.NotNull above guarantees warning — the MASKED URL is echoed
    }

    [Fact]
    public async Task DiscordTarget_SmallWebhookBody_Unaffected()
    {
        // Regression: normal small 429/400 bodies behave exactly as pre-T-057 — same results, and no over-cap warning fires. (The 400 mapping itself stays
        // pinned by the channel-type tests above.)

        // Small 429 body → retry-after parsed as today.
        {
            using var host = TestHost.Create();

            host.Server.Given(Request.Create())
                  .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Content-Type", "application/json").WithBody("""{"retry_after_ms":1500}"""));

            var result = await host.Target.SendAsync(MakePost());

            Assert.Equal(TargetOutcome.Retryable, result.Outcome);
            Assert.Equal(TimeSpan.FromMilliseconds(1500), result.RetryAfter);   // small bodies behave exactly as today…
            Assert.DoesNotContain("exceeds", string.Join("\n", host.Logger.Messages));   // …and no over-cap warning fires
        }

        // Small non-channel-type 400 body → permanent failure with the short excerpt, as today (channel-type 400 mapping stays pinned by the tests above).
        {
            using var host = TestHost.Create();

            host.Server.Given(Request.Create())
                  .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody("""{"message":"Webhook token invalid"}"""));

            var result = await host.Target.SendAsync(MakePost());

            Assert.Equal(TargetOutcome.PermanentFailure, result.Outcome);
            Assert.StartsWith("400:", result.Error!);   // ! safe: PermanentFailure always carries the formatted error (MapAttempt contract)
            Assert.Contains("Webhook token invalid", result.Error);   // short body excerpt preserved…
            Assert.DoesNotContain("exceeds", string.Join("\n", host.Logger.Messages));   // …with no over-cap warning
        }
    }

    [Fact]
    public async Task Send_500_ReturnsRetryableWithoutRetryAfter()
    {
        using var host = TestHost.Create();

        host.Server.Given(Request.Create())
                 .RespondWith(Response.Create().WithStatusCode(500).WithHeader("Content-Type", "application/json").WithBody("""{"message":"Internal error"}"""));

        var result = await host.Target.SendAsync(MakePost());

        Assert.Equal(TargetOutcome.Retryable, result.Outcome);
        Assert.Null(result.RetryAfter);   // 5xx carries no retry-after hint — the worker's backoff applies
    }

    [Fact]
    public async Task Send_Timeout_ReturnsRetryable()
    {
        using var host = TestHost.Create();   // client timeout is 2 s (test-only) — the server delays 5 s, so the client gives up first

        host.Server.Given(Request.Create())
                 .RespondWith(Response.Create().WithStatusCode(204).WithDelay(TimeSpan.FromSeconds(5)));

        var result = await host.Target.SendAsync(MakePost());

        Assert.Equal(TargetOutcome.Retryable, result.Outcome);
        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public async Task Send_403_ReturnsPermanentFailure()
    {
        using var host = TestHost.Create();

        host.Server.Given(Request.Create())
                 .RespondWith(Response.Create().WithStatusCode(403).WithHeader("Content-Type", "application/json").WithBody("""{"message":"Invalid Webhook Token"}"""));

        var result = await host.Target.SendAsync(MakePost());

        Assert.Equal(TargetOutcome.PermanentFailure, result.Outcome);
        Assert.StartsWith("403:", result.Error!);
        Assert.Contains("Invalid Webhook Token", result.Error);   // short body excerpt preserved
    }

    [Fact]
    public async Task Send_EmbedMode_ChannelType400_SwapsToForumOnceAndDelivers()
    {
        using var host = TestHost.Create(mode: "embed");

        // Call 1 → channel-type 400 (message-text signal); call 2 → 204. WireMock 2.x has no ordered stubs, so a callback counts calls.
        var calls = 0;
        host.Server.Given(Request.Create())
               .RespondWith(Response.Create().WithCallback(_ =>
                   Interlocked.Increment(ref calls) == 1
                       ? Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody(ChannelTypeMessageBody).ResponseMessage
                       : Response.Create().WithStatusCode(204).ResponseMessage));

        var result = await host.Target.SendAsync(MakePost());

        Assert.Equal(TargetOutcome.Delivered, result.Outcome);
        Assert.Equal(2, host.Entries.Count);   // exactly two requests — the swap happened once
        Assert.False(host.EntryBody(0).TryGetProperty("thread_name", out _));    // first attempt: embed mode
        Assert.True(host.EntryBody(1).TryGetProperty("thread_name", out _));     // second attempt: forum mode
    }

    [Fact]
    public async Task Send_ForumMode_ChannelType400_SwapsToEmbedOnceAndDelivers()
    {
        using var host = TestHost.Create(mode: "forum");

        // Call 1 → channel-type 400 (nested string-code signal); call 2 → 204. WireMock 2.x has no ordered stubs, so a callback counts calls.
        var calls = 0;
        host.Server.Given(Request.Create())
               .RespondWith(Response.Create().WithCallback(_ =>
                   Interlocked.Increment(ref calls) == 1
                       ? Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody(ChannelTypeCodeStringBody).ResponseMessage
                       : Response.Create().WithStatusCode(204).ResponseMessage));

        var result = await host.Target.SendAsync(MakePost());

        Assert.Equal(TargetOutcome.Delivered, result.Outcome);
        Assert.Equal(2, host.Entries.Count);
        Assert.True(host.EntryBody(0).TryGetProperty("thread_name", out _));     // first attempt: forum mode
        Assert.False(host.EntryBody(1).TryGetProperty("thread_name", out _));    // second attempt: embed mode
    }

    [Fact]
    public async Task Send_ChannelType400Twice_ReturnsPermanentFailureAfterExactlyTwoAttempts()
    {
        using var host = TestHost.Create(mode: "embed");

        // Every attempt gets the channel-type 400 (both signals present) — still never a third attempt.
        host.Server.Given(Request.Create())
                 .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody(ChannelTypeBothSignalsBody));

        var result = await host.Target.SendAsync(MakePost());

        Assert.Equal(TargetOutcome.PermanentFailure, result.Outcome);
        Assert.StartsWith("400:", result.Error!);
        Assert.Equal(2, host.Entries.Count);   // exactly two — the swap was one-shot
    }

    [Fact]
    public async Task Send_NeverLogsFullWebhookUrl_OnErrorPaths()
    {
        // A Discord-shaped webhook URL (so WebhookMasker actually masks it) with a stubbed handler — no network involved.
        const string webhookUrl = "https://discord.com/api/webhooks/1234567890/supersecrettoken";
        var masked = WebhookMasker.Mask(webhookUrl);

        // Error path 1: 4xx permanent failure.
        {
            var logger = new CapturingLogger();
            var services = new ServiceCollection();
            services.AddHttpClient(DiscordNotificationTarget.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(HttpStatusCode.Forbidden, """{"message":"Invalid Webhook Token"}"""));

            using (var provider = services.BuildServiceProvider())
            {
                var target = new DiscordNotificationTarget(provider.GetRequiredService<IHttpClientFactory>(), new DiscordTargetConfig { WebhookUrl = webhookUrl }, logger);

                var result = await target.SendAsync(MakePost());

                Assert.Equal(TargetOutcome.PermanentFailure, result.Outcome);
            }

            AssertLogsAreMasked(logger, webhookUrl, masked);
        }

        // Error path 2: transport failure.
        {
            var logger = new CapturingLogger();
            var services = new ServiceCollection();
            services.AddHttpClient(DiscordNotificationTarget.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(new HttpRequestException("connection refused")));

            using (var provider = services.BuildServiceProvider())
            {
                var target = new DiscordNotificationTarget(provider.GetRequiredService<IHttpClientFactory>(), new DiscordTargetConfig { WebhookUrl = webhookUrl }, logger);

                var result = await target.SendAsync(MakePost());

                Assert.Equal(TargetOutcome.Retryable, result.Outcome);
            }

            AssertLogsAreMasked(logger, webhookUrl, masked);
        }
    }

    private static Post MakePost(string title = "Hello", string? description = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ItemId = "item-1",
        Title = title,
        Description = description,
        Link = "https://example.com/item",
        Author = null,
        PublishedAt = new DateTimeOffset(2026, 10, 23, 14, 5, 9, TimeSpan.Zero),
        ImageUrl = null,
        SourceUrl = "https://example.com/feed.xml",
    };

    private static void AssertLogsAreMasked(CapturingLogger logger, string fullUrl, string? masked)
    {
        var all = string.Join("\n", logger.Messages);

        Assert.NotEmpty(logger.Messages);          // the error path actually logged something…
        Assert.DoesNotContain(fullUrl, all);       // …never with the full webhook URL (token included)…
        Assert.DoesNotContain("supersecrettoken", all);   // …nor the bare token…
        Assert.Contains(masked!, all);             // …and in masked form where echoed.
    }

    /// <summary>One WireMock server + a target pointed at it, with the named "discord" client wired (2 s timeout — test-only; production
    /// leaves it to a later task) and the default unnamed client failing fast if touched.</summary>
    private sealed class TestHost : IDisposable
    {
        public WireMockServer Server { get; }
        public DiscordNotificationTarget Target { get; }
        public CapturingLogger Logger { get; }

        private readonly IServiceProvider _services;

        private TestHost(WireMockServer server, DiscordNotificationTarget target, CapturingLogger logger, IServiceProvider services)
        {
            Server = server;
            Target = target;
            Logger = logger;
            _services = services;
        }

        public IReadOnlyList<WireMock.Logging.ILogEntry> Entries => Server.LogEntries;

        public JsonElement EntryBody(int index) => JsonSerializer.Deserialize<JsonElement>(Entries[index].RequestMessage!.Body!);   // ! safe: a logged request always carries its message and body (WireMock log contract)

        public static TestHost Create(string mode = "embed", int? color = null, string? fallbackImage = null)
        {
            var server = WireMockServer.Start();   // dynamic free port — no fixed binding

            var logger = new CapturingLogger();
            var services = new ServiceCollection();

            services.AddHttpClient("").ConfigurePrimaryHttpMessageHandler(() => new FailFastHandler());   // unnamed client → loud failure if touched
            services.AddHttpClient(DiscordNotificationTarget.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(2));

            var provider = services.BuildServiceProvider();
            var target = new DiscordNotificationTarget(
                provider.GetRequiredService<IHttpClientFactory>(),
                new DiscordTargetConfig { WebhookUrl = server.Url, Mode = mode, Color = color, FallbackImageUrl = fallbackImage },
                logger);

            return new TestHost(server, target, logger, provider);
        }

        public void Dispose()
        {
            Server.Dispose();
            if (_services is IDisposable disposable)   // BuildServiceProvider returns a ServiceProvider in practice — dispose when possible
                disposable.Dispose();
        }
    }

    /// <summary>Records every formatted log message at any level for later assertions.</summary>
    private sealed class CapturingLogger : ILogger<DiscordNotificationTarget>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add($"{logLevel}: {formatter(state, exception)}");
    }

    /// <summary>Thrown instead of sending when the default (unnamed) client is used — proof that the target uses the named "discord" client.</summary>
    private sealed class FailFastHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The default (unnamed) HTTP client was used — the target must POST through the named \"discord\" client.");
    }

    /// <summary>Returns a fixed response or throws a fixed exception — for error-path tests without a live server.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode? _status;
        private readonly string _body = string.Empty;
        private readonly Exception? _exception;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public StubHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_exception is null
                ? new HttpResponseMessage(_status!.Value) { Content = new StringContent(_body, Encoding.UTF8, "application/json") }   // ! safe: the status ctor sets _status
                : throw _exception);
    }
}
