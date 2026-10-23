using System.Net;
using System.Net.Sockets;
using NewsQuack.App.Fetching;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-010 throttle tests: the single global semaphore must never allow more than 8 in-flight requests and must be balanced on every
/// path — success, HTTP error, transport error, cancellation (the R8 property; T-041 pins it down later, this is its early form).</summary>
public class GlobalHttpThrottleTests
{
    [Fact]
    public async Task Throttle_ParallelFetches_NeverExceedsMaxConcurrency()
    {
        using var host = FeedFetcherTestHost.Create();

        var inFlight = 0;
        var maxObserved = 0;
        var gate = new object();

        // The stub holds each request for a moment and records the concurrent in-flight count via callback. robots.txt fetches (fail-open 404)
        // go through the same throttle, so this measures EVERY request the component makes.
        host.Server.Given(Request.Create().WithPath("/slow"))
              .RespondWith(Response.Create().WithCallback(async _ =>
              {
                  var current = Interlocked.Increment(ref inFlight);
                  lock (gate) if (current > maxObserved) maxObserved = current;

                  await Task.Delay(75);

                  Interlocked.Decrement(ref inFlight);
                  return Response.Create().WithStatusCode(200).WithBody("ok").ResponseMessage;   // IResponseMessage — the async WithCallback overload
              }));

        var url = $"{host.BaseUrl}/slow";
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => host.Fetcher.FetchAsync(url)));

        Assert.All(results, r => Assert.Equal(FetchStatus.Ok, r.Status));   // all 32 completed…
        Assert.True(maxObserved <= GlobalHttpThrottle.DefaultMaxConcurrent, $"observed {maxObserved} in-flight requests — the throttle allows at most {GlobalHttpThrottle.DefaultMaxConcurrent}");
        Assert.Equal(GlobalHttpThrottle.DefaultMaxConcurrent, host.Throttle.AvailableCount);   // …and every slot was released again
    }

    [Fact]
    public async Task Throttle_AllPathsRelease_CounterReturnsToInitial()
    {
        using var host = FeedFetcherTestHost.Create();

        host.Server.Given(Request.Create().WithPath("/ok")).RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));
        host.Server.Given(Request.Create().WithPath("/missing")).RespondWith(Response.Create().WithStatusCode(404));
        host.Server.Given(Request.Create().WithPath("/slow")).RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)));

        var closedPort = GetClosedPort();   // connection refused → the TransportError path

        var ok = await host.Fetcher.FetchAsync($"{host.BaseUrl}/ok");
        var httpError = await host.Fetcher.FetchAsync($"{host.BaseUrl}/missing");
        var transportError = await host.Fetcher.FetchAsync($"http://127.0.0.1:{closedPort}/feed.xml");

        Assert.Equal(FetchStatus.Ok, ok.Status);
        Assert.Equal(FetchStatus.HttpError, httpError.Status);
        Assert.Equal(FetchStatus.TransportError, transportError.Status);

        // Cancellation mid-request — the slot must be released on this path too.
        using var cts = new CancellationTokenSource();
        var slow = host.Fetcher.FetchAsync($"{host.BaseUrl}/slow", cts.Token);
        await Task.Delay(100);   // let the request start (the stub holds it for 5 s)
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);

        Assert.Equal(GlobalHttpThrottle.DefaultMaxConcurrent, host.Throttle.AvailableCount);   // back to the initial value — nothing leaked on any path
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
