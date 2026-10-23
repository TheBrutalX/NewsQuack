using NewsQuack.App.Fetching;
using Microsoft.Extensions.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-010 robots integration tests: the origin-level gate in front of every feed fetch — disallow blocks WITHOUT fetching the feed,
/// 404/failure fails open (with a Warning log where applicable), and the decision is cached per origin for the process lifetime.</summary>
public class FeedFetcherRobotsTests
{
    [Fact]
    public async Task Fetch_RobotsDisallowsFeed_ReturnsRobotsBlockedWithoutFetchingFeed()
    {
        using var host = FeedFetcherTestHost.Create();

        host.Server.Given(Request.Create().WithPath("/robots.txt"))
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("User-agent: *\nDisallow: /private/"));

        var result = await host.Fetcher.FetchAsync($"{host.BaseUrl}/private/feed.xml");

        Assert.Equal(FetchStatus.RobotsBlocked, result.Status);
        Assert.Single(host.IndicesOf("/robots.txt"));   // robots.txt was consulted…
        Assert.Empty(host.IndicesOf("/private/feed.xml"));   // …and the feed itself was never fetched
    }

    [Fact]
    public async Task Fetch_Robots404_FailsOpenAndFetchesFeed()
    {
        using var host = FeedFetcherTestHost.Create();   // no /robots.txt stub → WireMock answers 404 by default

        host.Server.Given(Request.Create().WithPath("/feed")).RespondWith(Response.Create().WithStatusCode(200).WithBody("<rss/>"));

        var result = await host.Fetcher.FetchAsync($"{host.BaseUrl}/feed");

        Assert.Equal(FetchStatus.Ok, result.Status);   // 404 robots → fail open…
        Assert.Single(host.IndicesOf("/robots.txt"));  // …the robots check happened…
        Assert.Single(host.IndicesOf("/feed"));        // …and the feed was fetched anyway
    }

    [Fact]
    public async Task Fetch_Robots500_FailsOpenWithWarningLog()
    {
        using var host = FeedFetcherTestHost.Create();

        host.Server.Given(Request.Create().WithPath("/robots.txt")).RespondWith(Response.Create().WithStatusCode(500));
        host.Server.Given(Request.Create().WithPath("/feed")).RespondWith(Response.Create().WithStatusCode(200).WithBody("<rss/>"));

        var result = await host.Fetcher.FetchAsync($"{host.BaseUrl}/feed");

        Assert.Equal(FetchStatus.Ok, result.Status);   // a 500 from robots.txt fails open — the feed still arrives…
        Assert.NotNull(host.FetcherLogs.Find(LogLevel.Warning, "robots.txt", "failing open"));   // …and the failure is observable in the log
    }

    [Fact]
    public async Task Fetch_SameOriginTwice_RobotsFetchedOnce()
    {
        using var host = FeedFetcherTestHost.Create();

        host.Server.Given(Request.Create().WithPath("/robots.txt"))
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("User-agent: *\nAllow: /"));
        host.Server.Given(Request.Create().WithPath("/feed1")).RespondWith(Response.Create().WithStatusCode(200).WithBody("one"));
        host.Server.Given(Request.Create().WithPath("/feed2")).RespondWith(Response.Create().WithStatusCode(200).WithBody("two"));

        var first = await host.Fetcher.FetchAsync($"{host.BaseUrl}/feed1");
        var second = await host.Fetcher.FetchAsync($"{host.BaseUrl}/feed2");

        Assert.Equal(FetchStatus.Ok, first.Status);
        Assert.Equal(FetchStatus.Ok, second.Status);
        Assert.Single(host.IndicesOf("/robots.txt"));   // the decision is cached per origin — no second robots fetch for the same origin
    }
}
