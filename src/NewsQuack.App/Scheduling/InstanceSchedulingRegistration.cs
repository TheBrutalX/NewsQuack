using NewsQuack.App.Configuration;   // NewsQuackConfig / InstanceConfig / FeedConfig — one scheduler per configured instance
using NewsQuack.App.Fetching;        // IFeedFetcher — the fetch layer under the global throttle (T-010)
using NewsQuack.App.Pipeline;        // PostPipelineRunner — the ordered stage chain (T-024)
using NewsQuack.App.Services;        // IFeedErrorTracker — backoff + auto-remove (T-016 + decision 7)
using NewsQuack.Core;                // IFeedParser — two-pass, per-item isolation (T-012/T-013)
using Microsoft.Extensions.DependencyInjection;   // IServiceCollection + ServiceDescriptor (plain Add — see the per-instance registration below)
using Microsoft.Extensions.Hosting;   // IHostedService — the host owns each scheduler's start/stop/cancellation
using Microsoft.Extensions.Logging;   // ILogger<T>

namespace NewsQuack.App.Scheduling;

/// <summary>
/// T-024: DI wiring for per-instance scheduling, shared by both host paths in Program.cs (same pattern as AddStateStore / AddPostPipeline).
/// Registers ONE <see cref="InstanceScheduler"/> hosted service per configured instance — the host owns its start/stop/cancellation. The poll
/// interval is converted here from the validated <c>CheckIntervalMinutes</c> (1..1440) to a <see cref="TimeSpan"/>; the scheduler itself never
/// reads config, so tests can pass small intervals directly. No config keys invented — everything comes from the frozen T-002 schema.
/// </summary>
public static class InstanceSchedulingRegistration
{
    public static IServiceCollection AddInstanceScheduling(this IServiceCollection services, NewsQuackConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        foreach (var instance in config.Instances ?? [])
        {
            // ! safe: boot validation guarantees every Id is non-null/unique and CheckIntervalMinutes is 1..1440 — the guards below only prevent a null dereference.
            if (instance.Id is null) continue;
            if (instance.CheckIntervalMinutes is not { } minutes) continue;

            var pollInterval = TimeSpan.FromMinutes(minutes);   // registration converts CheckIntervalMinutes → TimeSpan (decision 6)
            var feeds = instance.Feeds ?? [];                    // validated: at least one feed per instance

            services.Add(ServiceDescriptor.Singleton<IHostedService>(sp => new InstanceScheduler(   // plain Add (NOT AddHostedService): since .NET 9 that overload is TryAddEnumerable, which would silently drop every instance after the first — one scheduler per configured instance is the point (found while wiring T-021's workers)
                instance.Id,
                pollInterval,
                feeds,
                sp.GetRequiredService<IFeedFetcher>(),        // T-010 — the fetch layer under the global throttle
                sp.GetRequiredService<IFeedErrorTracker>(),   // T-016 — backoff + auto-remove (+ decision 7's BackoffRemainingAsync)
                sp.GetRequiredService<IFeedParser>(),         // T-012/T-013 — two-pass, per-item isolation
                sp.GetRequiredService<PostPipelineRunner>(),  // T-024 — the ordered stage chain (resolves IPostStage, never concrete types)
                sp.GetRequiredService<IServiceScopeFactory>(),// scoped LogEnricher (T-004) — one scope per cycle and per feed
                sp.GetRequiredService<ILogger<InstanceScheduler>>())));   // closes InstanceScheduler( / Singleton( / Add( — one descriptor per configured instance
        }

        return services;
    }
}
