using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NewsQuack.App.Fetching;

/// <summary>
/// T-010: DI wiring for the feed-fetching layer, shared by both host paths in Program.cs (same pattern as AddNewsquackHttpClients). One
/// <see cref="IFeedFetcher"/> serves all instances/feeds — multi-instance mode shares the global throttle by design.
/// </summary>
public static class FeedFetcherRegistration
{
    public static IServiceCollection AddFeedFetching(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Fixed 8 slots for v1.0 — no config key invented (coordinator decision). One instance for the whole process.
        services.AddSingleton<GlobalHttpThrottle>();

        // Bounded per-URL UA cache, default cap 1000, process lifetime.
        services.AddSingleton(new UaCache());

        // The validated fallback-UA cascade — singleton so construction-time validation (and its Warning logs) happens exactly once.
        services.AddSingleton(sp => new UserAgentResolver(null, sp.GetService<ILogger<UserAgentResolver>>()));

        // Later tasks (schedulers/pipeline) resolve IFeedFetcher — never the concrete type.
        services.AddSingleton<IFeedFetcher, FeedFetcher>();

        return services;
    }
}
