using NewsQuack.App.Configuration;
using NewsQuack.App.Fetching;
using NewsQuack.App.Http;
using Microsoft.Extensions.DependencyInjection;
using WireMock.Server;

namespace NewsQuack.App.Tests;

/// <summary>T-010 test host: one WireMock server on a dynamic port + the REAL production HTTP wiring (AddNewsQuackHttpClients → named "feed"
/// client with the shared SsrfSafeHttpHandler) + a FeedFetcher built around it. Test-visible knobs exactly where the task allows injection:
/// feed timeout (seconds), UA cache cap, and the candidate list. The throttle is NOT a knob — the plans assert against the production constant.</summary>
internal sealed class FeedFetcherTestHost : IDisposable
{
    public WireMockServer Server { get; }

    public string BaseUrl => Server.Url!;   // ! safe: a started server always has its Url set (WireMock contract — T-005/T-020 test hosts rely on the same)

    public FeedFetcher Fetcher { get; }

    public GlobalHttpThrottle Throttle { get; }

    public UserAgentResolver Resolver { get; }

    public LogCapture FetcherLogs { get; }

    public LogCapture ResolverLogs { get; }

    private readonly IServiceProvider _services;

    private FeedFetcherTestHost(WireMockServer server, IServiceProvider services, FeedFetcher fetcher, GlobalHttpThrottle throttle, UserAgentResolver resolver, LogCapture fetcherLogs, LogCapture resolverLogs)
    {
        Server = server;
        _services = services;
        Fetcher = fetcher;
        Throttle = throttle;
        Resolver = resolver;
        FetcherLogs = fetcherLogs;
        ResolverLogs = resolverLogs;
    }

    public IReadOnlyList<WireMock.Logging.ILogEntry> Entries => Server.LogEntries;

    public static FeedFetcherTestHost Create(int feedTimeoutSeconds = 5, int? uaCacheCapacity = null, IEnumerable<string?>? userAgents = null)
    {
        var server = WireMockServer.Start();   // dynamic free port — no fixed binding (CI-safe)

        var services = new ServiceCollection();
        services.AddNewsQuackHttpClients(new HttpSectionConfig { FeedTimeoutSeconds = feedTimeoutSeconds });
        var provider = services.BuildServiceProvider();

        var fetcherLogs = new LogCapture();
        var resolverLogs = new LogCapture();

        // The throttle is always the production constant (8) — the plans assert against it, so it must not be a knob.
        var throttle = new GlobalHttpThrottle();
        var resolver = new UserAgentResolver(userAgents, resolverLogs.For<UserAgentResolver>());
        var fetcher = new FeedFetcher(
            provider.GetRequiredService<IHttpClientFactory>(),
            throttle,
            resolver,
            uaCacheCapacity is { } capacity ? new UaCache(capacity) : null,   // default cap 1000 when not injected (the production default)
            fetcherLogs.For<FeedFetcher>());

        return new FeedFetcherTestHost(server, provider, fetcher, throttle, resolver, fetcherLogs, resolverLogs);
    }

    /// <summary>The absolute path of the Nth logged request (0-based).</summary>
    public string RequestPath(int index) => Entries[index].RequestMessage!.AbsolutePath;   // ! safe: a logged request always carries its message (WireMock log contract — T-005/T-020 tests rely on the same)

    /// <summary>The User-Agent header of the Nth logged request; empty when none was sent.</summary>
    public string UserAgentAt(int index)
    {
        var headers = Entries[index].RequestMessage!.Headers;   // ! safe: a logged request always carries its message (same WireMock log contract as RequestPath)
        return headers is not null && headers.FirstOrDefault(kv => kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)).Value is { Count: > 0 } values
            ? values[0]
            : string.Empty;
    }

    /// <summary>The 0-based indices of all logged requests whose absolute path equals <paramref name="path"/> — the way these tests count "exactly N requests" to a URL.</summary>
    public int[] IndicesOf(string path) => Entries.Select((entry, index) => index).Where(index => RequestPath(index) == path).ToArray();

    public void Dispose()
    {
        Server.Dispose();
        if (_services is IDisposable disposable)   // BuildServiceProvider returns a ServiceProvider in practice — dispose when possible (same pattern as T-005/T-020 test hosts)
            disposable.Dispose();
    }
}
