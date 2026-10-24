using NewsQuack.App.Configuration;   // NewsquackConfig — the registry is built from the validated config at composition time (constraint 3)
using Microsoft.Extensions.DependencyInjection;   // IServiceCollection — one singleton for the whole process

namespace NewsQuack.App.Discord;

/// <summary>
/// T-048: DI wiring for the shared pacer registry — one registration method called from BOTH host paths in Program.cs (same pattern as AddSendWorkers). The
/// registry is a singleton built once at composition time from the full validated config (constraint 3, SendQueueRegistry precedent): every configured webhook
/// URL gets one shared pacer, instances sharing a URL share pacing state, strictest interval wins on conflict. The "discord" client's pacing handler resolves
/// it lazily when a client is created, so registration order relative to AddNewsquackHttpClients does not matter.
/// </summary>
public static class WebhookPacingRegistration
{
    public static IServiceCollection AddWebhookPacing(this IServiceCollection services, NewsquackConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddSingleton(new WebhookPacerRegistry(config));   // one shared registry for the whole process — pacing state is per URL, never per instance (constraint 3)

        return services;
    }
}
