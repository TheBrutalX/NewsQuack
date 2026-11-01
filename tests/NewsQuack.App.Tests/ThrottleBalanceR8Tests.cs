using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NewsQuack.App.Fetching;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-041: the R8 regression in its full interleaved-parallel form — one mixed workload through the REAL fetcher (T-010's test host, production wiring)
/// that covers EVERY throttle path — success, HTTP error, transport error, cancellation mid-request, and cancellation while WAITING for a slot — must
/// leave <see cref="GlobalHttpThrottle.AvailableCount"/> at its initial value. T-010's <c>Throttle_AllPathsRelease_CounterReturnsToInitial</c> is the early
/// SEQUENTIAL form; this adds the parallel interleaving and the queued-cancellation path (slot never taken ⇒ nothing released). Red on any implementation
/// that leaks a slot (counter stays below 8) or double-releases one (<c>Release()</c> throws / overshoots).
/// </summary>
public class ThrottleBalanceR8Tests
{
    /// <summary>The slow endpoint's hold time — comfortably above every settle window in this test, below the host's 5 s client timeout, so held requests finish as Ok.</summary>
    private const int SlowEndpointSeconds = 3;

    /// <summary>Budget for the mixed batch — it completes in ~1 s normally; exceeding it means some implementation starves requests (e.g. a fully leaked throttle), and the test must fail fast instead of hanging CI.</summary>
    private static readonly TimeSpan BatchBudget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AllPathsInterleavedParallel_CounterReturnsToInitial()
    {
        using var host = FeedFetcherTestHost.Create();

        host.Server.Given(Request.Create().WithPath("/ok")).RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));
        host.Server.Given(Request.Create().WithPath("/missing")).RespondWith(Response.Create().WithStatusCode(404));
        host.Server.Given(Request.Create().WithPath("/slow")).RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(SlowEndpointSeconds)));

        var closedPort = GetClosedPort();   // connection refused → the TransportError path

        // ── Phase 1: warm up the robots decision for the WireMock origin (one 2xx fetch) so every later request in this test goes through exactly one
        // throttle wait — no concurrent first-time robots fetches muddying which path is under test.
        var warmup = await host.Fetcher.FetchAsync($"{host.BaseUrl}/ok");
        Assert.Equal(FetchStatus.Ok, warmup.Status);

        // ── Phase 2: the mixed parallel workload — success + HTTP error + transport error interleaved (each closed-port fetch also exercises a failed
        // robots.txt fetch on its own origin, which fails open; every one of those requests goes through the throttle too).
        var batch = new[]
        {
            host.Fetcher.FetchAsync($"{host.BaseUrl}/ok"),                        // success path
            host.Fetcher.FetchAsync($"{host.BaseUrl}/ok"),                        // success path
            host.Fetcher.FetchAsync($"{host.BaseUrl}/missing"),                   // HTTP error path (404)
            host.Fetcher.FetchAsync($"{host.BaseUrl}/missing"),                   // HTTP error path (404)
            host.Fetcher.FetchAsync($"http://127.0.0.1:{closedPort}/feed.xml"),   // transport error path (connection refused)
            host.Fetcher.FetchAsync($"http://127.0.0.1:{closedPort}/other.xml"),  // transport error path (connection refused)
        };

        var results = await Task.WhenAll(batch).WaitAsync(BatchBudget);   // bounded: a fully leaked throttle would starve the batch — fail fast, don't hang CI

        Assert.Equal(FetchStatus.Ok, results[0].Status);
        Assert.Equal(FetchStatus.Ok, results[1].Status);
        Assert.Equal(FetchStatus.HttpError, results[2].Status);
        Assert.Equal(404, results[2].StatusCode);
        Assert.Equal(FetchStatus.HttpError, results[3].Status);
        Assert.Equal(404, results[3].StatusCode);
        Assert.Equal(FetchStatus.TransportError, results[4].Status);
        Assert.Equal(FetchStatus.TransportError, results[5].Status);

        // ── Phase 3: cancellation MID-REQUEST on the slow endpoint — the slot was taken and must be released by this path too.
        using (var cts = new CancellationTokenSource())
        {
            var midRequest = host.Fetcher.FetchAsync($"{host.BaseUrl}/slow", cts.Token);

            await Task.Delay(150);   // let the request get in flight (the stub holds it for SlowEndpointSeconds)
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => midRequest);
        }

        // ── Phase 4: cancellation while WAITING FOR A SLOT — hold all slots with slow requests, queue one more fetch, cancel it before it can take a slot.
        var held = Enumerable.Range(0, GlobalHttpThrottle.DefaultMaxConcurrent)
            .Select(_ => host.Fetcher.FetchAsync($"{host.BaseUrl}/slow"))
            .ToList();

        // Provably fully held BEFORE the waiter is cancelled — this assertion is what keeps the next step deterministic (no flake).
        await UntilTrue(() => host.Throttle.AvailableCount == 0, TimeSpan.FromSeconds(10), "all slots to be taken by the held requests");

        using var waiterCts = new CancellationTokenSource();
        var waiter = host.Fetcher.FetchAsync($"{host.BaseUrl}/slow", waiterCts.Token);

        await Task.Delay(50);   // let it reach the slot wait — with all slots held it cannot proceed past WaitAsync, so it is queued by now
        waiterCts.Cancel();     // cancelled while QUEUED: no slot was ever taken, so this path must release NOTHING

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        var heldResults = await Task.WhenAll(held);   // the held requests finish and release their slots

        Assert.All(heldResults, r => Assert.Equal(FetchStatus.Ok, r.Status));   // they completed normally — no client timeout in between (3 s < 5 s)

        // ── R8: back to the initial value — nothing leaked or double-released on any of the five paths.
        Assert.Equal(GlobalHttpThrottle.DefaultMaxConcurrent, host.Throttle.AvailableCount);
    }

    /// <summary>Polls until the condition holds or fails the test with a timeout — deterministic waiting without a fixed-sleep guess.</summary>
    private static async Task UntilTrue(Func<bool> condition, TimeSpan timeout, string what)
    {
        var stopwatch = Stopwatch.StartNew();

        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
                throw new TimeoutException($"Timed out after {timeout} waiting for {what}.");

            await Task.Delay(10);
        }
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
