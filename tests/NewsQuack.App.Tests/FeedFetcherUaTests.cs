using NewsQuack.App.Fetching;
using Microsoft.Extensions.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-010 UA cascade tests: the validated fallback-UA cascade (401/403 → next candidate, each tried at most once), the bounded LRU
/// winner cache per URL, and construction-time validation of candidates.</summary>
public class FeedFetcherUaTests
{
    [Fact]
    public async Task Fetch_200WithDefaultUa_ReturnsOkAndSendsCandidateOne()
    {
        using var host = FeedFetcherTestHost.Create();

        host.Server.Given(Request.Create().WithPath("/feed")).RespondWith(Response.Create().WithStatusCode(200).WithBody("<rss/>"));

        var result = await host.Fetcher.FetchAsync($"{host.BaseUrl}/feed");

        Assert.Equal(FetchStatus.Ok, result.Status);
        Assert.Equal("<rss/>", result.Content);

        var feedIndices = host.IndicesOf("/feed");
        Assert.Single(feedIndices);   // no cascade needed — exactly one request…
        Assert.Equal(UserAgentResolver.DefaultCandidates[0], host.UserAgentAt(feedIndices[0]));   // …with candidate #1 (the default UA)
    }

    [Fact]
    public async Task Fetch_403Then200_RetriesWithSecondCandidateAndCachesWinner()
    {
        using var host = FeedFetcherTestHost.Create();

        host.Server.Given(Request.Create().WithPath("/feed").WithHeader("User-Agent", UserAgentResolver.DefaultCandidates[0]))
              .RespondWith(Response.Create().WithStatusCode(403));
        host.Server.Given(Request.Create().WithPath("/feed").WithHeader("User-Agent", UserAgentResolver.DefaultCandidates[1]))
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("<rss/>"));

        var first = await host.Fetcher.FetchAsync($"{host.BaseUrl}/feed");

        Assert.Equal(FetchStatus.Ok, first.Status);

        var feedIndices = host.IndicesOf("/feed");
        Assert.Equal(2, feedIndices.Length);   // candidate #1 rejected (403) → exactly one retry with candidate #2
        Assert.Equal(UserAgentResolver.DefaultCandidates[0], host.UserAgentAt(feedIndices[0]));
        Assert.Equal(UserAgentResolver.DefaultCandidates[1], host.UserAgentAt(feedIndices[1]));

        // The winner is cached for this URL: the second fetch goes straight to candidate #2 — a single request.
        var second = await host.Fetcher.FetchAsync($"{host.BaseUrl}/feed");

        Assert.Equal(FetchStatus.Ok, second.Status);

        var feedIndicesAfterSecond = host.IndicesOf("/feed");
        Assert.Equal(3, feedIndicesAfterSecond.Length);   // exactly one more request…
        Assert.Equal(UserAgentResolver.DefaultCandidates[1], host.UserAgentAt(feedIndicesAfterSecond[2]));   // …directly with the cached winner
    }

    [Fact]
    public async Task Fetch_AllCandidates403_ReturnsLastHttpErrorAfterExactlyTwoRequests()
    {
        using var host = FeedFetcherTestHost.Create();

        host.Server.Given(Request.Create().WithPath("/feed")).RespondWith(Response.Create().WithStatusCode(403));

        var result = await host.Fetcher.FetchAsync($"{host.BaseUrl}/feed");

        Assert.Equal(FetchStatus.HttpError, result.Status);
        Assert.Equal(403, result.StatusCode);   // the LAST error (both candidates got 403)
        Assert.Equal(2, host.IndicesOf("/feed").Length);   // exactly two requests — strictly bounded by the candidate count
    }

    [Fact]
    public void UaCache_BoundedCap_EvictsOldest()
    {
        var cache = new UaCache(3);

        cache.Set("https://a/feed", "ua-a");
        cache.Set("https://b/feed", "ua-b");
        cache.Set("https://c/feed", "ua-c");
        cache.Set("https://d/feed", "ua-d");   // over the cap of 3…

        Assert.False(cache.TryGet("https://a/feed", out _));   // …the first (oldest) entry is evicted…
        Assert.True(cache.TryGet("https://b/feed", out var b));
        Assert.Equal("ua-b", b);                              // …and the rest survive with their values intact
        Assert.True(cache.TryGet("https://c/feed", out _));
        Assert.True(cache.TryGet("https://d/feed", out _));
    }

    [Fact]
    public void UaCache_ReadRefreshesRecency_LeastRecentlyUsedEvicted()
    {
        var cache = new UaCache(3);

        cache.Set("a", "ua-a");
        cache.Set("b", "ua-b");
        cache.Set("c", "ua-c");

        Assert.True(cache.TryGet("a", out _));   // "a" becomes most-recently-used…
        cache.Set("d", "ua-d");                  // …so the eviction takes "b" (the least recently used), not "a"

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.True(cache.TryGet("d", out _));
    }

    [Fact]
    public async Task UaResolver_InvalidCandidateAtConstruction_DroppedWithWarningNotSent()
    {
        using var host = FeedFetcherTestHost.Create(userAgents: new string?[]
        {
            null,                                   // not a candidate at all…
            "   ",                                  // …empty after trim…
            new string('x', UserAgentResolver.MaxCandidateLength + 1),   // …over the 255-char cap…
            "has\u0001control",                     // …a control character…
            "Valid/1.0 (+https://example.com)",     // …and one valid candidate that must be the only one ever sent
        });

        Assert.Equal(new[] { "Valid/1.0 (+https://example.com)" }, host.Resolver.Candidates);   // the four invalid ones are dropped…
        Assert.Equal(4, host.ResolverLogs.CountAt(LogLevel.Warning));                            // …each with a Warning log

        host.Server.Given(Request.Create().WithPath("/feed")).RespondWith(Response.Create().WithStatusCode(200).WithBody("<rss/>"));

        var result = await host.Fetcher.FetchAsync($"{host.BaseUrl}/feed");

        Assert.Equal(FetchStatus.Ok, result.Status);
        Assert.Equal("Valid/1.0 (+https://example.com)", host.UserAgentAt(host.IndicesOf("/feed")[0]));   // only the valid candidate was sent — never an invalid one
    }
}
