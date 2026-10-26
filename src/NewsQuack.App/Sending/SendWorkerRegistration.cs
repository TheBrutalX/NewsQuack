using System.Globalization;   // CultureInfo.InvariantCulture — the drain-timeout parse never uses CurrentCulture (AGENTS.md)
using NewsQuack.App.Configuration;   // NewsQuackConfig / InstanceConfig — one worker (and its target) per configured instance
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
/// OWN target, never another instance's. No config keys invented — everything comes from the frozen T-002 schema; the one operational env var is the
/// T-031 shutdown drain window (NEWSQUACK_SHUTDOWN_DRAIN_TIMEOUT_SECONDS), same pattern as NEWSQUACK_DATABASE.
/// </summary>
public static class SendWorkerRegistration
{
    /// <summary>T-031 decision 4: the operational env var holding the shutdown drain window in seconds (not a schema key — same pattern as NEWSQUACK_DATABASE / NEWSQUACK_DEADLETTER_WEBHOOK).</summary>
    public const string DrainTimeoutEnvironmentVariable = "NEWSQUACK_SHUTDOWN_DRAIN_TIMEOUT_SECONDS";

    public static IServiceCollection AddSendWorkers(this IServiceCollection services, NewsQuackConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        // T-031 decision 4: read the operational env var once at wiring time (T-022 pattern); each worker factory parses it with its own logger so an invalid value warns exactly when the workers are constructed.
        var rawDrainTimeout = Environment.GetEnvironmentVariable(DrainTimeoutEnvironmentVariable);

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
                sp.GetRequiredService<ILogger<SendWorker>>(),
                drainTimeout: ParseDrainTimeout(rawDrainTimeout, sp.GetRequiredService<ILogger<SendWorker>>()))));   // T-031 decision 4: the parsed env var → this worker's drain window (default 30 s) — closes SendWorker( / Singleton( / Add( — one descriptor per configured instance
        }

        return services;
    }

    /// <summary>
    /// T-031 decision 4: parses <see cref="DrainTimeoutEnvironmentVariable"/>. Missing/empty ⇒ the 30 s default (no warning — an unset operational env var is normal);
    /// a positive integer ⇒ that many seconds; anything else (invalid or non-positive) ⇒ the 30 s default + one Warning, so the host always keeps a sane window.
    /// Internal static taking the raw value string so tests pin it without mutating process env (T-022 pattern).
    /// </summary>
    internal static TimeSpan ParseDrainTimeout(string? rawValue, ILogger<SendWorker> logger)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return TimeSpan.FromSeconds(SendWorker.DefaultDrainTimeoutSeconds);   // missing/empty ⇒ default

        if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);   // a positive integer of seconds — UTC-agnostic duration math, no culture involved

        logger.LogWarning("{EnvVar} value '{Value}' is not a positive integer of seconds — falling back to the default drain timeout of {DefaultSeconds} s", DrainTimeoutEnvironmentVariable, rawValue, SendWorker.DefaultDrainTimeoutSeconds);
        return TimeSpan.FromSeconds(SendWorker.DefaultDrainTimeoutSeconds);   // invalid/non-positive ⇒ default (never an unbounded or zero window)
    }
}
