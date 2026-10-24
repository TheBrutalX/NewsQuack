using System.Text.Json;   // JsonDocument — asserting the notification JSON body field by field
using NewsQuack.App.Http;  // NamedHttpClientRegistration.DiscordClientName — the named "discord" client (frozen name, T-005)
using NewsQuack.App.Sending;   // DeadLetterNotifier / DeadLetterNotification + AddDeadLettering's internal CreateNotifier factory (InternalsVisibleTo)
using Microsoft.Extensions.DependencyInjection;   // ServiceCollection — minimal container: named client (+ fail-fast default client)
using Microsoft.Extensions.Logging;   // LogLevel — log assertions
using WireMock.RequestBuilders;       // Request — stubbing the webhook server
using WireMock.ResponseBuilders;      // Response — 204/500 answers
using WireMock.Server;                // WireMockServer — dynamic port, CI-safe (T-020 pattern)
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-022: the system-webhook notifier. The notifier is pointed at a self-hosted WireMock on a dynamic port (CI-safe, T-020 pattern); the named "discord" client is proven by failing fast if the default (unnamed)
/// client is ever used. URL validation is unit-tested through AddDeadLettering's internal static CreateNotifier factory — no process env mutation (decision 7).
/// </summary>
public class DeadLetterNotifierTests
{
    private const string FeedUrl = "https://example.com/feed.xml";

    /// <summary>A dead-letter event exactly as the sink formats it — ISO-8601 UTC failed-at.</summary>
    private static DeadLetterNotification MakeNotification() => new(
        "inst-1", FeedUrl, "item-9", "Broken post", "5xx", "HTTP 503", "2026-10-23T12:00:00.0000000Z");

    [Fact]
    public async Task Notifier_WebhookConfigured_PostsNotificationJson()
    {
        using var host = Host.Create();

        host.Server.Given(Request.Create()).RespondWith(Response.Create().WithStatusCode(204));

        await host.Notifier.NotifyAsync(MakeNotification());

        Assert.Single(host.Server.LogEntries);   // exactly one POST per dead-letter event (decision 5)
        using var document = JsonDocument.Parse(host.Server.LogEntries[0].RequestMessage!.Body!);   // ! safe: a logged request always carries its message and body (WireMock log contract, T-020 pattern)
        var root = document.RootElement;

        Assert.Equal("inst-1", root.GetProperty("InstanceId").GetString());                          // instance id...
        Assert.Equal(FeedUrl, root.GetProperty("FeedUrl").GetString());                              // ...feed url...
        Assert.Equal("item-9", root.GetProperty("ItemId").GetString());                              // ...item id...
        Assert.Equal("Broken post", root.GetProperty("Title").GetString());                          // ...title (from the post)...
        Assert.Equal("5xx", root.GetProperty("ErrorCategory").GetString());                          // ...error category...
        Assert.Equal("HTTP 503", root.GetProperty("LastError").GetString());                         // ...last error...
        Assert.Equal("2026-10-23T12:00:00.0000000Z", root.GetProperty("FailedAtUtc").GetString());   // ...failed-at ISO-8601 UTC
    }

    [Fact]
    public async Task Notifier_HttpFailure_SwallowsAndLogsWarning()
    {
        // Failure 1: a non-success HTTP status — no throw, Warning logged (no exception argument — there is no exception for a received response).
        {
            using var host = Host.Create();

            host.Server.Given(Request.Create()).RespondWith(Response.Create().WithStatusCode(500));

            await host.Notifier.NotifyAsync(MakeNotification());   // must not throw (decision 5)

            Assert.NotNull(host.Capture.Find(LogLevel.Warning, "returned HTTP 500"));
        }

        // Failure 2: a transport-level exception — caught with the exception as argument (stack trace survives), Warning logged.
        {
            var capture = new LogCapture();
            var services = new ServiceCollection();
            services.AddHttpClient(NamedHttpClientRegistration.DiscordClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new ThrowingHandler(new HttpRequestException("connection refused")));

            using (var provider = services.BuildServiceProvider())
            {
                var notifier = DeadLetterRegistration.CreateNotifier(
                    "https://example.com/hook", provider.GetRequiredService<IHttpClientFactory>(), capture.For<DeadLetterNotifier>());

                await notifier.NotifyAsync(MakeNotification());   // must not throw (decision 5)
            }

            var warning = capture.Find(LogLevel.Warning, "connection refused");
            Assert.NotNull(warning);
            Assert.NotNull(warning!.Exception);   // ! safe: NotNull above guarantees the entry — exception as argument (AGENTS.md)
        }
    }

    [Fact]
    public async Task Notifier_MissingOrInvalidUrl_IsDisabledNoOp()
    {
        var capture = new LogCapture();

        var services = new ServiceCollection();
        services.AddHttpClient(NamedHttpClientRegistration.DiscordClientName);

        using (var provider = services.BuildServiceProvider())
        {
            var factory = provider.GetRequiredService<IHttpClientFactory>();

            // Missing/empty ⇒ disabled no-op — constructed without throwing, never posts, and logs nothing at all.
            foreach (var url in new string?[] { null, "", "   " })
            {
                var notifier = DeadLetterRegistration.CreateNotifier(url, factory, capture.For<DeadLetterNotifier>());

                Assert.False(notifier.IsEnabled);
                await notifier.NotifyAsync(MakeNotification());   // no-op — never throws
            }

            Assert.Empty(capture.Snapshot());   // missing/empty is a silent no-op (decision 5) — only an INVALID url warns

            // Present but not an absolute http(s) URL ⇒ disabled with a construction-time Warning.
            foreach (var url in new[] { "ftp://example.com/hook", "relative/path", "not a url" })
            {
                var notifier = DeadLetterRegistration.CreateNotifier(url, factory, capture.For<DeadLetterNotifier>());

                Assert.False(notifier.IsEnabled);
                await notifier.NotifyAsync(MakeNotification());   // still a no-op — never posts
            }

            Assert.Equal(3, capture.CountAt(LogLevel.Warning));   // exactly one construction-time Warning per invalid URL...
            Assert.NotNull(capture.Find(LogLevel.Warning, "ftp://example.com/hook"));   // ...naming the offending value
        }
    }

    /// <summary>One WireMock server + a notifier pointed at it, with the named "discord" client wired and the default unnamed client failing fast if touched (T-020 pattern).</summary>
    private sealed class Host : IDisposable
    {
        private readonly IServiceProvider _services;

        public Host(WireMockServer server, DeadLetterNotifier notifier, LogCapture capture, IServiceProvider services)
        {
            Server = server;
            Notifier = notifier;
            Capture = capture;
            _services = services;   // BuildServiceProvider returns a ServiceProvider in practice — kept concrete so Dispose can release it (T-020 pattern)
        }

        public WireMockServer Server { get; }
        public DeadLetterNotifier Notifier { get; }
        public LogCapture Capture { get; }

        public static Host Create()
        {
            var server = WireMockServer.Start();   // dynamic free port — no fixed binding (CI-safe)

            var capture = new LogCapture();
            var services = new ServiceCollection();
            services.AddHttpClient("").ConfigurePrimaryHttpMessageHandler(() => new FailFastHandler());   // unnamed client → loud failure if touched (named-client proof, T-020 pattern)
            services.AddHttpClient(NamedHttpClientRegistration.DiscordClientName);

            var provider = services.BuildServiceProvider();
            var notifier = DeadLetterRegistration.CreateNotifier(
                server.Url,   // a started WireMockServer always has its Url set (WireMock contract — same as DiscordTargetSendTests)
                provider.GetRequiredService<IHttpClientFactory>(), capture.For<DeadLetterNotifier>());

            return new Host(server, notifier, capture, provider);
        }

        public void Dispose()
        {
            Server.Dispose();
            if (_services is IDisposable disposable)   // dispose when possible (T-020 pattern)
                disposable.Dispose();
        }
    }

    /// <summary>Thrown instead of sending when the default (unnamed) client is used — proof that the notifier POSTs through the named "discord" client.</summary>
    private sealed class FailFastHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The default (unnamed) HTTP client was used — the notifier must POST through the named \"discord\" client.");
    }

    /// <summary>Throws a fixed exception on every send — for the transport-failure path without a live server.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw _exception;
    }
}
