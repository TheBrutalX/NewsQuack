using NewsQuack.App.Configuration;   // NewsQuackConfig / InstanceConfig — one sink per configured instance
using NewsQuack.Core;                // IStateStore — the frozen contract the sink persists through (T-015)
using Microsoft.Extensions.DependencyInjection;   // IServiceCollection + ServiceDescriptor (keyed-singleton pattern, T-021)
using Microsoft.Extensions.Logging;   // ILogger<T>

namespace NewsQuack.App.Sending;

/// <summary>
/// T-022: DI wiring for dead-letter persistence + the system webhook notifier, shared by both host paths in Program.cs (same pattern as AddSendWorkers). Registers ONE DeadLetterNotifier singleton — built
/// through an internal static factory that takes the env-var value string, so unit tests pin URL validation without mutating process env (decision 7) — and ONE keyed DeadLetterSink singleton per configured
/// instance. No config keys invented: the notifier URL is an operational env var (NEWSQUACK_DEADLETTER_WEBHOOK), same pattern as NEWSQUACK_DATABASE.
/// </summary>
public static class DeadLetterRegistration
{
    public static IServiceCollection AddDeadLettering(this IServiceCollection services, NewsQuackConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        // The operational env var is read once at wiring time and handed to the internal static factory (decision 7). Missing/empty ⇒ disabled no-op; invalid ⇒ disabled with a construction-time Warning.
        var webhookUrl = Environment.GetEnvironmentVariable(DeadLetterNotifier.WebhookEnvironmentVariable);

        services.AddSingleton(sp => CreateNotifier(
            webhookUrl,
            sp.GetRequiredService<IHttpClientFactory>(),   // the named "discord" client (T-005) — registered on both host paths before this
            sp.GetRequiredService<ILogger<DeadLetterNotifier>>()));

        foreach (var instance in config.Instances ?? [])
        {
            // ! safe: boot validation guarantees every Id is non-null/unique — the guard below only prevents a null dereference (same as AddSendWorkers).
            if (instance.Id is null) continue;

            var instanceId = instance.Id;

            // One keyed singleton sink per configured instance (T-021 pattern): the Post carries no instance id, so each worker resolves ITS OWN sink — never another instance's.
            services.Add(ServiceDescriptor.KeyedSingleton<DeadLetterSink>(instanceId, (sp, _) => new DeadLetterSink(
                instanceId,
                sp.GetRequiredService<IStateStore>(),              // T-015 — persist dead letters through the frozen contract
                sp.GetRequiredService<DeadLetterNotifier>(),       // the shared notifier — one ops webhook for all instances
                sp.GetRequiredService<ILogger<DeadLetterSink>>())));   // closes DeadLetterSink( / KeyedSingleton( / Add( — one descriptor per configured instance
        }

        return services;
    }

    /// <summary>Internal static factory taking the env-var value string — unit tests pin URL validation without mutating process env (decision 7).</summary>
    internal static DeadLetterNotifier CreateNotifier(string? webhookUrl, IHttpClientFactory httpFactory, ILogger<DeadLetterNotifier> logger)
        => new(webhookUrl, httpFactory, logger);
}
