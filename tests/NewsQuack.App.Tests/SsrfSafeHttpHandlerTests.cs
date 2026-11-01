using System.IO.Compression;
using System.Net;
using System.Text;
using NewsQuack.App.Configuration;
using NewsQuack.App.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-005: redirect and decompression behavior of the shared SsrfSafeHttpHandler, exercised through the REAL client wiring
/// (AddNewsQuackHttpClients) against a self-hosted WireMock on dynamic ports (CI-safe). The default server binds to localhost, which
/// accepts both 127.0.0.1 and ::1 connections — proven before writing these tests — so loopback-IP redirect targets are deterministic.</summary>
public class SsrfSafeHttpHandlerTests
{
    [Fact]
    public async Task Redirect_TwoHopsThen200_FollowsAndReturnsFinalBody()
    {
        using var host = Host.Create();

        host.Server.Given(Request.Create().WithPath("/hop1")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", "/hop2"));
        host.Server.Given(Request.Create().WithPath("/hop2")).RespondWith(Response.Create().WithStatusCode(301).WithHeader("Location", "/final"));
        host.Server.Given(Request.Create().WithPath("/final")).RespondWith(Response.Create().WithStatusCode(200).WithBody("final-body"));

        using var response = await host.Client.GetAsync($"{host.BaseUrl}/hop1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("final-body", await response.Content.ReadAsStringAsync());
        Assert.Equal(3, host.Server.LogEntries.Count);   // both redirects followed — the client reached /final
    }

    [Fact]
    public async Task Redirect_303_SecondHopReceivedAsGetWithoutBody()
    {
        using var host = Host.Create();

        host.Server.Given(Request.Create().WithPath("/a")).RespondWith(Response.Create().WithStatusCode(303).WithHeader("Location", "/b"));
        host.Server.Given(Request.Create().WithPath("/b")).RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        using var response = await host.Client.PostAsync($"{host.BaseUrl}/a", new StringContent("original-body"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ! safe: a logged request always carries its message (WireMock log contract — same as DiscordTargetSendTests)
        var second = host.Server.LogEntries[1].RequestMessage!;
        Assert.Equal("GET", second.Method);                            // 303 always becomes GET…
        Assert.True(string.IsNullOrEmpty(second.Body));                // …with the body dropped
    }

    [Fact]
    public async Task Redirect_FourHops_ThrowsTooManyRedirects()
    {
        using var host = Host.Create();

        for (var i = 1; i <= 4; i++)   // each hop redirects to the next — a 5th response would be needed to finish
            host.Server.Given(Request.Create().WithPath($"/hop{i}")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"/hop{i + 1}"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => host.Client.GetAsync($"{host.BaseUrl}/hop1"));

        Assert.Contains("Too many redirects", ex.Message);   // exactly 3 hops allowed — the 4th redirect fails with a clear message
        Assert.Equal(4, host.Server.LogEntries.Count);       // …and it failed AT hop 4, not earlier
    }

    [Fact]
    public async Task Redirect_ToFtpScheme_ThrowsWithLocationInMessage()
    {
        using var host = Host.Create();

        const string target = "ftp://files.example.com/archive/feed.xml";   // hostile feed redirecting off http/https
        host.Server.Given(Request.Create().WithPath("/a")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", target));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => host.Client.GetAsync($"{host.BaseUrl}/a"));

        Assert.Contains(target, ex.Message);   // the offending location is named (it's a feed URL, not a secret)
    }

    [Fact]
    public async Task Redirect_EmptyLocation_Throws()
    {
        using var host = Host.Create();

        host.Server.Given(Request.Create().WithPath("/a")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", ""));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => host.Client.GetAsync($"{host.BaseUrl}/a"));

        Assert.Contains("Location", ex.Message);   // a 3xx without a usable Location is an error, never a silent pass-through
    }

    [Fact]
    public async Task Redirect_BlockPrivateOn_ToLoopbackIp_Rejected()
    {
        using var host = Host.Create(blockPrivateNetworkRedirects: true);

        host.Server.Given(Request.Create().WithPath("/a")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"http://127.0.0.1:{host.Server.Port}/loopback"));
        host.Server.Given(Request.Create().WithPath("/loopback")).RespondWith(Response.Create().WithStatusCode(200).WithBody("should-not-arrive"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => host.Client.GetAsync($"{host.BaseUrl}/a"));

        Assert.Contains("127.0.0.1", ex.Message);   // exception names the offending location
        Assert.Single(host.Server.LogEntries);      // rejected before following — no second request was sent
    }

    [Fact]
    public async Task Redirect_BlockPrivateOff_ToLoopbackIp_Allowed()
    {
        using var host = Host.Create(blockPrivateNetworkRedirects: false);   // switch off → no address check at all (T-058: false is no longer the schema default)

        host.Server.Given(Request.Create().WithPath("/a")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"http://127.0.0.1:{host.Server.Port}/loopback"));
        host.Server.Given(Request.Create().WithPath("/loopback")).RespondWith(Response.Create().WithStatusCode(200).WithBody("loopback-body"));

        using var response = await host.Client.GetAsync($"{host.BaseUrl}/a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("loopback-body", await response.Content.ReadAsStringAsync());   // the redirect into loopback was followed
    }

    [Fact]
    public async Task Redirect_BlockPrivateOn_DnsNameResolvingToPrivate_Rejected()
    {
        using var host = Host.Create(blockPrivateNetworkRedirects: true);

        host.Server.Given(Request.Create().WithPath("/a")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"http://localhost:{host.Server.Port}/local"));
        host.Server.Given(Request.Create().WithPath("/local")).RespondWith(Response.Create().WithStatusCode(200).WithBody("should-not-arrive"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => host.Client.GetAsync($"{host.BaseUrl}/a"));

        Assert.Contains("localhost", ex.Message);   // "localhost" resolves to loopback — the DNS name is checked via its resolved addresses
    }

    [Fact]
    public async Task RedirectTo_UnspecifiedIpv4_FlagOn_Rejected()
    {
        using var host = Host.Create(blockPrivateNetworkRedirects: true);

        // T-058: INADDR_ANY (all-zero IPv4) is in the blocklist — rejected by validation before hop 2 is sent.
        host.Server.Given(Request.Create().WithPath("/a")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"http://{IPAddress.Any}:{host.Server.Port}/x"));
        host.Server.Given(Request.Create().WithPath("/x")).RespondWith(Response.Create().WithStatusCode(200).WithBody("should-not-arrive"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => host.Client.GetAsync($"{host.BaseUrl}/a"));

        Assert.Contains(IPAddress.Any.ToString(), ex.Message);           // the exception names the blocked address…
        Assert.Contains("blocked private-network range", ex.Message);    // …as a validation rejection, not a connect failure
        Assert.Single(host.Server.LogEntries);                           // rejected before following — no second request was sent

        // Flag off — validation lets the unspecified address through (no SSRF rejection) and the hop is followed; it then fails only in .NET's own
        // socket layer, which refuses to connect to an unspecified target (Dns literal check, stable since corefx #37734). A non-.NET client
        // following the same Location would reach loopback on Linux — exactly the bypass this task closes.
        using var hostOff = Host.Create(blockPrivateNetworkRedirects: false);

        hostOff.Server.Given(Request.Create().WithPath("/a")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"http://{IPAddress.Any}:{hostOff.Server.Port}/x"));
        hostOff.Server.Given(Request.Create().WithPath("/x")).RespondWith(Response.Create().WithStatusCode(200).WithBody("loopback-arrived"));

        var exOff = await Assert.ThrowsAsync<HttpRequestException>(() => hostOff.Client.GetAsync($"{hostOff.BaseUrl}/a"));

        Assert.DoesNotContain("rejected", exOff.Message);            // not our SSRF rejection — validation let the hop through…
        Assert.Contains("unspecified addresses", exOff.Message);     // …it is .NET's own connect-level refusal of the unspecified target
    }

    [Fact]
    public async Task Redirect_DefaultHttpSectionConfig_ToPrivateIpLiteral_Rejected()
    {
        // T-058 default flip: a client built from `new HttpSectionConfig()` (no explicit key) blocks private-network redirects by default.
        using var host = Host.Create(new HttpSectionConfig());

        const string target = "http://10.255.255.1/feed.xml";   // 10/8 — validation-only, no live listener needed (rejected before hop 2 is sent); no explicit port: Uri renders default ports away
        host.Server.Given(Request.Create().WithPath("/a")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", target));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => host.Client.GetAsync($"{host.BaseUrl}/a"));

        Assert.Contains(target, ex.Message);                          // the offending location is named…
        Assert.Contains("blocked private-network range", ex.Message); // …as a validation rejection — the schema default is on
    }

    [Fact]
    public async Task Redirect_HttpsToHttp_LogsDowngradeWarning()
    {
        using var httpsOrigin = WireMockServer.Start(useSSL: true);   // self-signed cert (Kestrel default) — TlsHandler accepts any cert
        using var httpTarget = WireMockServer.Start();

        httpsOrigin.Given(Request.Create().WithPath("/a")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"{httpTarget.Url}/b"));
        httpTarget.Given(Request.Create().WithPath("/b")).RespondWith(Response.Create().WithStatusCode(200).WithBody("downgraded-body"));

        var capture = new LogCapture();
        using var handler = TlsHandler(capture);
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync($"{httpsOrigin.Url}/a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("downgraded-body", await response.Content.ReadAsStringAsync());   // the downgrade hop was followed — never rejected…

        var warning = Assert.Single(capture.Snapshot(), e => e.Level == LogLevel.Warning);   // …and logged exactly once…
        Assert.Contains($"{httpsOrigin.Url}/a", warning.Message);                              // …naming both URLs (from → to)
        Assert.Contains($"{httpTarget.Url}/b", warning.Message);
    }

    [Fact]
    public async Task Redirect_HttpToHttp_NoDowngradeWarning()
    {
        var capture = new LogCapture();
        using var host = Host.Create(blockPrivateNetworkRedirects: false, logCapture: capture);

        host.Server.Given(Request.Create().WithPath("/a")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", "/b"));
        host.Server.Given(Request.Create().WithPath("/b")).RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        using var response = await host.Client.GetAsync($"{host.BaseUrl}/a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, capture.CountAt(LogLevel.Warning));   // same-scheme hop — no downgrade warning (no noise on redirecting feeds)
    }

    [Fact]
    public async Task Redirect_HttpsToHttps_NoDowngradeWarning()
    {
        using var server = WireMockServer.Start(useSSL: true);

        server.Given(Request.Create().WithPath("/a")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", "/b"));   // relative — stays on the https origin
        server.Given(Request.Create().WithPath("/b")).RespondWith(Response.Create().WithStatusCode(200).WithBody("still-secure"));

        var capture = new LogCapture();
        using var handler = TlsHandler(capture);
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync($"{server.Url}/a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, capture.CountAt(LogLevel.Warning));   // same-scheme hop — no downgrade warning
    }

    /// <summary>The handler exactly as the production wiring builds it, except with a cert-accepting inner handler: the DI path has no hook for TLS settings, and these tests need a real https origin (self-signed WireMock cert).</summary>
    private static SsrfSafeHttpHandler TlsHandler(LogCapture capture) =>
        new(false, new HttpClientHandler { AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true }, capture.For<SsrfSafeHttpHandler>());

    [Fact]
    public async Task Gzip_ResponseWithContentEncodingGzip_BodyDecompressedTransparently()
    {
        using var host = Host.Create();

        const string payload = "the quick brown fox jumps over the lazy dog";
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(payload));

        host.Server.Given(Request.Create().WithPath("/gz"))
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Encoding", "gzip").WithBody(compressed.ToArray()));

        var body = await host.Client.GetStringAsync($"{host.BaseUrl}/gz");

        Assert.Equal(payload, body);   // native Accept-Encoding: gzip — no manual header handling anywhere in the service
    }

    /// <summary>One WireMock server + a "feed" client built through the exact production wiring (AddNewsQuackHttpClients) on a dynamic port.</summary>
    private sealed class Host : IDisposable
    {
        public WireMockServer Server { get; }
        public HttpClient Client { get; }
        public string BaseUrl => Server.Url!;   // ! safe: a started server always has its Url set (WireMock contract — T-020's tests rely on the same)

        private readonly IServiceProvider _services;

        private Host(WireMockServer server, HttpClient client, IServiceProvider services)
        {
            Server = server;
            Client = client;
            _services = services;
        }

        public static Host Create(bool blockPrivateNetworkRedirects = false, LogCapture? logCapture = null)
            => Create(new HttpSectionConfig { BlockPrivateNetworkRedirects = blockPrivateNetworkRedirects }, logCapture);

        /// <summary>Build with the given section — pass `new HttpSectionConfig()` (no explicit key) to pin the schema defaults (T-058: private-network redirect block on by default).</summary>
        public static Host Create(HttpSectionConfig config, LogCapture? logCapture = null)
        {
            var server = WireMockServer.Start();   // dynamic free port — no fixed binding

            var services = new ServiceCollection();
            services.AddNewsQuackHttpClients(config);
            if (logCapture is not null)
                services.AddSingleton(logCapture.For<SsrfSafeHttpHandler>());   // the shared handler resolves its logger from DI at first client creation
            var provider = services.BuildServiceProvider();

            return new Host(server, provider.GetRequiredService<IHttpClientFactory>().CreateClient("feed"), provider);
        }

        public void Dispose()
        {
            Client.Dispose();   // the client owns no handlers of its own (the shared one is owned by the provider) — disposed for symmetry
            Server.Dispose();
            if (_services is IDisposable disposable)   // BuildServiceProvider returns a ServiceProvider in practice — dispose when possible (same pattern as DiscordTargetSendTests)
                disposable.Dispose();
        }
    }
}
