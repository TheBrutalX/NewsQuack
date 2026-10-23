using System.IO.Compression;
using System.Net;
using System.Text;
using NewsQuack.App.Configuration;
using NewsQuack.App.Http;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-005: redirect and decompression behavior of the shared SsrfSafeHttpHandler, exercised through the REAL client wiring
/// (AddNewsquackHttpClients) against a self-hosted WireMock on dynamic ports (CI-safe). The default server binds to localhost, which
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
        using var host = Host.Create(blockPrivateNetworkRedirects: false);   // switch off (the schema default) → no address check at all

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

    /// <summary>One WireMock server + a "feed" client built through the exact production wiring (AddNewsquackHttpClients) on a dynamic port.</summary>
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

        public static Host Create(bool blockPrivateNetworkRedirects = false)
        {
            var server = WireMockServer.Start();   // dynamic free port — no fixed binding

            var services = new ServiceCollection();
            services.AddNewsquackHttpClients(new HttpSectionConfig { BlockPrivateNetworkRedirects = blockPrivateNetworkRedirects });
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
