using NewsQuack.Core;   // IDedupEngine / IPostStage — the frozen contracts implemented here
using Microsoft.Extensions.DependencyInjection;

namespace NewsQuack.App.Pipeline;

/// <summary>
/// T-014: DI wiring for the dedup layer, shared by both host paths in Program.cs (same pattern as AddFeedErrorTracking). Both types are stateless
/// singletons serving all instances/feeds — thread-safety comes from the store (one context per operation). The engine depends on the IStateStore
/// singleton registered by AddStateStore; ILogger&lt;T&gt; comes from the host's standard logging registration. No config keys invented.
/// </summary>
public static class DedupEngineRegistration
{
    public static IServiceCollection AddDedupEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Later tasks (the send worker / pipeline runner) resolve IDedupEngine — never the concrete type. No caching in v1.0: every check is a
        // single indexed SQLite lookup through IStateStore (a cache is a later-SP optimization, T-014 decision 1).
        services.AddSingleton<IDedupEngine, DedupEngine>();

        // The pipeline runner (a later task) orders and executes stages — it resolves IPostStage instances, never concrete types. Order 10: dedup
        // runs before any enrichment work; the IsolateItem failure policy is declared by the stage itself.
        services.AddSingleton<IPostStage, DedupCheckStage>();

        return services;
    }
}
