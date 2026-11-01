using System.Net;
using System.Net.Sockets;
using System.Text;
using NewsQuack.App.Fetching;
using Microsoft.Extensions.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-057 body-cap tests: an over-cap feed body is a visible TransportError (the capped read stops at MaxFeedBytes+1 — a hostile multi-GB answer cannot
/// OOM the container), a body of exactly MaxFeedBytes still parses Ok (boundary pin), and plaintext-HTTP feeds warn exactly once per distinct URL per process
/// lifetime regardless of fetch outcome.</summary>
public class FeedFetcherBodyCapTests
{
    [Fact]
    public async Task FeedFetcher_BodyLargerThanCap_ReturnsTransportErrorWithoutReadingWholeBody()
    {
        using var host = FeedFetcherTestHost.Create();

        // 9 MiB of XML-ish bytes — well over the 8 MiB cap. With the old unbounded read this would have come back Ok with a 9 MiB string;
        // now the capped read stops at MaxFeedBytes+1 and the fetch is an observable TransportError instead.
        var body = new byte[FeedFetcher.MaxFeedBytes + 1024 * 1024];
        for (var i = 0; i < body.Length; i++)
            body[i] = (byte)'a';

        host.Server.Given(Request.Create().WithPath("/feed"))
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/xml").WithBody(body));

        var result = await host.Fetcher.FetchAsync($"{host.BaseUrl}/feed");

        Assert.Equal(FetchStatus.TransportError, result.Status);   // over cap → TransportError…
        Assert.Null(result.Content);                               // …Content stays null for non-Ok…
        Assert.Equal(200, result.StatusCode);                      // …the status that was seen is preserved…
        Assert.Contains("exceeds", result.Error!);                 // ! safe: a non-Ok status always carries a reason (record contract) — and the reason names the cap breach
        var warning = host.FetcherLogs.Find(LogLevel.Warning, "exceeds", $"{FeedFetcher.MaxFeedBytes}");   // the Warning carries the cap value…
        Assert.NotNull(warning);                                   // …so an over-cap feed is visible in the log, never a silent drop
    }

    [Fact]
    public async Task FeedFetcher_BodyExactlyAtCap_ParsesOk()
    {
        using var host = FeedFetcherTestHost.Create();

        // Valid tiny XML padded with whitespace to EXACTLY MaxFeedBytes bytes — the boundary pin: over-cap means LONGER than the cap, so a body of exactly
        // MaxFeedBytes is still Ok (mirrors ReadCappedAsync's existing robots semantics).
        const string head = "<rss><channel><title>";
        const string tail = "</title></channel></rss>";
        var payload = head + new string(' ', FeedFetcher.MaxFeedBytes - head.Length - tail.Length) + tail;
        Assert.Equal(FeedFetcher.MaxFeedBytes, Encoding.UTF8.GetByteCount(payload));   // ASCII-only — byte count == char count

        host.Server.Given(Request.Create().WithPath("/feed"))
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/xml").WithBody(Encoding.UTF8.GetBytes(payload)));

        var result = await host.Fetcher.FetchAsync($"{host.BaseUrl}/feed");

        Assert.Equal(FetchStatus.Ok, result.Status);                  // exactly at the cap is still Ok…
        Assert.Equal(payload, result.Content);                        // …and the content comes back intact (byte-identical round-trip)…
        Assert.Null(host.FetcherLogs.Find(LogLevel.Warning, "exceeds"));   // …with no over-cap warning
    }

    [Fact]
    public async Task FeedFetcher_HttpFeedUrl_WarnsOncePerUrlAcrossMultipleFetches()
    {
        using var host = FeedFetcherTestHost.Create();

        host.Server.Given(Request.Create().WithPath("/feed-a")).RespondWith(Response.Create().WithStatusCode(200).WithBody("<rss/>"));
        host.Server.Given(Request.Create().WithPath("/feed-b")).RespondWith(Response.Create().WithStatusCode(200).WithBody("<rss/>"));

        var urlA = $"{host.BaseUrl}/feed-a";
        for (var i = 0; i < 3; i++)   // three poll cycles of the same URL — the guard must swallow all but the first
            Assert.Equal(FetchStatus.Ok, (await host.Fetcher.FetchAsync(urlA)).Status);   // healthy fetches — the warning must not depend on outcome

        Assert.Single(PlaintextWarningsFor(host.FetcherLogs, urlA));   // exactly ONE Warning for URL A…

        var urlB = $"{host.BaseUrl}/feed-b";
        for (var i = 0; i < 2; i++)
            Assert.Equal(FetchStatus.Ok, (await host.Fetcher.FetchAsync(urlB)).Status);

        Assert.Single(PlaintextWarningsFor(host.FetcherLogs, urlB));   // …a second distinct http:// URL warns once too…
        Assert.Single(PlaintextWarningsFor(host.FetcherLogs, urlA));   // …and URL A is never re-warned across the cycles

        // an https:// URL never warns — even when the fetch itself fails (no listener → TransportError); the warning is about the SCHEME, not the outcome
        var closedPort = GetClosedPort();
        var urlC = $"https://127.0.0.1:{closedPort}/secure";
        var result = await host.Fetcher.FetchAsync(urlC);

        Assert.Equal(FetchStatus.TransportError, result.Status);   // the failure is irrelevant here…
        Assert.Empty(PlaintextWarningsFor(host.FetcherLogs, urlC));   // …but no plaintext-HTTP warning for the https URL
    }

    /// <summary>The plaintext-HTTP warnings mentioning <paramref name="url"/> — the only Warning template that names a feed fetched over plain HTTP.</summary>
    private static List<LogCapture.Entry> PlaintextWarningsFor(LogCapture logs, string url) =>
        logs.Snapshot().Where(e => e.Level == LogLevel.Warning && e.Message.Contains("plaintext HTTP") && e.Message.Contains(url)).ToList();

    /// <summary>A port that is free but has no listener — connections to it are refused (same pattern as FeedFetcherStatusTests).</summary>
    private static int GetClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);   // bind port 0 → the OS picks a free port…
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();                                          // …then close it — connections to it are refused from now on
        return port;
    }
}
