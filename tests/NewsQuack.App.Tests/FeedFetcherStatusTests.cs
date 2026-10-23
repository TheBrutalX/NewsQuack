using System.Net;
using System.Net.Sockets;
using System.Text;
using NewsQuack.App.Fetching;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-010 status-mapping tests: 2xx → Ok (UTF-8 content), non-cascading 4xx → HttpError with the code and exactly one request, client
/// timeout → Timeout (not TransportError), connection refused → TransportError, caller cancellation → OperationCanceledException (never a status).</summary>
public class FeedFetcherStatusTests
{
    [Fact]
    public async Task Fetch_200_ReturnsOkWithUtf8Content()
    {
        using var host = FeedFetcherTestHost.Create();

        // Non-ASCII payload, Content-Type WITHOUT a charset — the fetcher must still decode it as UTF-8 (ReadAsStringAsync would fall back to ISO-8859-1).
        const string payload = "<rss><title>héllo wörld — 日本語</title></rss>";
        host.Server.Given(Request.Create().WithPath("/feed"))
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/xml").WithBody(Encoding.UTF8.GetBytes(payload)));

        var result = await host.Fetcher.FetchAsync($"{host.BaseUrl}/feed");

        Assert.Equal(FetchStatus.Ok, result.Status);
        Assert.Equal(payload, result.Content);   // byte-identical round-trip — UTF-8 decoding proven…
        Assert.Equal(200, result.StatusCode);    // …and the status is preserved on Ok as well
    }

    [Fact]
    public async Task Fetch_404_ReturnsHttpErrorWithCodeAndNoCascadeRetry()
    {
        using var host = FeedFetcherTestHost.Create();

        host.Server.Given(Request.Create().WithPath("/feed")).RespondWith(Response.Create().WithStatusCode(404));

        var result = await host.Fetcher.FetchAsync($"{host.BaseUrl}/feed");

        Assert.Equal(FetchStatus.HttpError, result.Status);
        Assert.Equal(404, result.StatusCode);   // the code is preserved…
        Assert.NotNull(result.Error);            // …and a non-Ok status always carries a human-readable reason…
        Assert.Single(host.IndicesOf("/feed"));  // …with exactly one request — 404 does not cascade (only 401/403 do)
    }

    [Fact]
    public async Task Fetch_ClientTimeout_ReturnsTimeoutNotTransportError()
    {
        using var host = FeedFetcherTestHost.Create(feedTimeoutSeconds: 2);   // test-only timeout — production keeps the config value

        host.Server.Given(Request.Create().WithPath("/feed"))
              .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)));   // the server answers after the client gave up

        var result = await host.Fetcher.FetchAsync($"{host.BaseUrl}/feed");

        Assert.Equal(FetchStatus.Timeout, result.Status);   // the per-client timeout (T-005) is its own status — not TransportError…
        Assert.Contains("timed out", result.Error!);        // …and the reason says so
    }

    [Fact]
    public async Task Fetch_ConnectionRefused_ReturnsTransportError()
    {
        using var host = FeedFetcherTestHost.Create();

        var closedPort = GetClosedPort();   // no listener — every connection to this port is refused (feed AND robots fail, robots fails open)

        var result = await host.Fetcher.FetchAsync($"http://127.0.0.1:{closedPort}/feed.xml");

        Assert.Equal(FetchStatus.TransportError, result.Status);
        Assert.Null(result.StatusCode);   // no HTTP response was ever seen…
        Assert.NotNull(result.Error);     // …but the failure is still observable — never a silent empty result
    }

    [Fact]
    public async Task Fetch_CallerCancellation_PropagatesOperationCanceledException()
    {
        using var host = FeedFetcherTestHost.Create();

        host.Server.Given(Request.Create().WithPath("/feed"))
              .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)));

        using var cts = new CancellationTokenSource();
        var fetch = host.Fetcher.FetchAsync($"{host.BaseUrl}/feed", cts.Token);
        await Task.Delay(100);   // let the request start (the stub holds it for 5 s)
        cts.Cancel();

        // Caller cancellation is NEVER mapped to a FetchStatus — it propagates so the caller can decide what "cancelled" means.
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.True(cts.Token.IsCancellationRequested);   // it was OUR token that fired, not the client's timeout
    }

    /// <summary>A port that is free but has no listener — connections to it are refused (deterministic TransportError without a live server).</summary>
    private static int GetClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);   // bind port 0 → the OS picks a free port…
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();                                          // …then close it — connections to it are refused from now on
        return port;
    }
}
