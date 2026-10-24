using NewsQuack.App.Configuration;   // NewsquackConfig / InstanceConfig — one worker (and its target) per configured instance
using NewsQuack.App.Discord;         // DiscordNotificationTarget — the v1.0 target (T-020), wired into DI here for the first time
using NewsQuack.App.Pipeline;        // SendQueueRegistry — this instance's bounded queue (T-024)
using NewsQuack.Core;                // INotificationTarget / IDedupEngine — the frozen contracts
using Microsoft.Extensions.DependencyInjection;   // IServiceCollection + ServiceDescriptor (plain Add — see the per-instance registration below)
using Microsoft.Extensions.Hosting;   // IHostedService — the host owns each worker's start/stop/cancellation
using Microsoft.Extensions.Logging;   // ILogger<T>

namespace NewsQuack.App.Sending;

/// <summary>
/// T-021: DI wiring for per-instance delivery, shared by both host paths in Program.cs (same pattern as AddInstanceScheduling). Registers ONE singleton
/// INotificationTarget and ONE SendWorker hosted service per configured instance — the per-instance factory closures are what make "one instance per
/// configured instance" true without touching the worker (decision 7). The target is a KEYED singleton keyed by instance id so each worker resolves ITS
/// OWN target, never another instance's. No config keys invented — everything comes from the frozen T-002 schema.
/// </summary>
public static class SendWorkerRegistration
{
    public static IServiceCollection AddSendWorkers(this IServiceCollection services, NewsquackConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        foreach (var instance in config.Instances ?? [])
        {
            // ! safe: boot validation guarantees every Id is non-null/unique and every instance has a Discord section with a valid webhook URL — the guard below only prevents a null dereference.
            if (instance.Id is null) continue;

            var instanceId = instance.Id;

            // One singleton target per configured instance, keyed by instance id (decision 7): v1.0 has exactly one target type — Discord. The closure
            // captures THIS instance's validated Discord config, so N instances get N targets, each with its own webhook URL. Keyed registration via the
            // ServiceDescriptor factory (the framework exposes no AddKeyed* collection extension) — resolved below with GetRequiredKeyedService.
            services.Add(ServiceDescriptor.KeyedSingleton<INotificationTarget>(instanceId, (sp, _) => new DiscordNotificationTarget(
                sp.GetRequiredService<IHttpClientFactory>(),
                instance.Discord!,   // ! safe: boot validation guarantees a non-null Discord section with a valid webhook URL (decision 7)
                sp.GetRequiredService<ILogger<DiscordNotificationTarget>>())));

            services.Add(ServiceDescriptor.Singleton<IHostedService>(sp => new SendWorker(   // plain Add (NOT AddHostedService): since .NET 9 that overload is TryAddEnumerable, which would silently drop every instance after the first — one descriptor per configured instance is the point
                instanceId,
                sp.GetRequiredService<SendQueueRegistry>().For(instanceId),   // this instance's queue — the SAME one SendStage enqueues into (T-024)
                sp.GetRequiredKeyedService<INotificationTarget>(instanceId),  // THIS instance's target — keyed so instances never share a target
                sp.GetRequiredService<IDedupEngine>(),                        // T-014 — confirm ONLY after Delivered (at-least-once)
                sp.GetRequiredService<IServiceScopeFactory>(),                // scoped LogEnricher (T-004) — one scope per post
                sp.GetRequiredKeyedService<DeadLetterSink>(instanceId),       // T-022: THIS instance's dead-letter sink — keyed so instances never share a sink (AddDeadLettering)
                sp.GetRequiredService<ILogger<SendWorker>>())));   // closes SendWorker( / Singleton( / Add( — one descriptor per configured instance
        }

        return services;
    }
}
