using NewsQuack.Core;   // IStateStore — resolved lazily from AddStateStore's singleton registration
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NewsQuack.App.Services;

/// <summary>
/// T-016: DI wiring for the per-feed error tracker, shared by both host paths in Program.cs (same pattern as AddFeedFetching). One singleton serves
/// all instances/feeds. The frozen T-002 schema has no auto-remove/threshold keys, so the issue defaults (enableAutoRemove=true,
/// autoRemoveThreshold=10) are applied at wiring time — no config keys invented (T-016 decision 2).
/// </summary>
public static class FeedErrorTrackingRegistration
{
    public static IServiceCollection AddFeedErrorTracking(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Later tasks (the scheduler) resolve IFeedErrorTracker — never the concrete type. The tracker depends on the IStateStore singleton, which
        // owns its per-operation contexts internally; ILogger<T> comes from the host's standard logging registration.
        services.AddSingleton<IFeedErrorTracker>(sp => new FeedErrorTracker(
            sp.GetRequiredService<IStateStore>(),
            sp.GetRequiredService<ILogger<FeedErrorTracker>>()));   // issue defaults (true, 10) via the constructor

        return services;
    }
}
