using System.Net;
using NewsQuack.App.Configuration;
using NewsQuack.App.Discord;      // T-048: WebhookPacingHandler / WebhookPacerRegistry — pacing on the "discord" client's pipeline
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NewsQuack.App.Http;

/// <summary>
/// T-005: the single HTTP wiring shared by BOTH host paths in Program.cs — three named clients (feed/discord/image) with per-client
/// timeouts from the validated config, and one shared <see cref="SsrfSafeHttpHandler"/> instance in every client's pipeline. The
/// "discord" name is frozen here (T-020's DiscordNotificationTarget already resolves it). No retries, caching or proxy settings — non-goals.
/// T-048: the "discord" client additionally carries a <see cref="Discord.WebhookPacingHandler"/> so EVERY request to a webhook URL is paced at the
/// configured per-URL minimum interval (shared across instances via the DI-resolved registry; null registry ⇒ transparent pass-through for bare test hosts).
/// </summary>
/// <remarks>
/// The shared handler is installed as the PRIMARY handler (ConfigurePrimaryHttpMessageHandler), not via AddHttpMessageHandler: Microsoft.Extensions.Http
/// rejects additional handlers whose InnerHandler is already set ("must not be reused or cached"), and a DelegatingHandler that owns its inner
/// HttpClientHandler always has one — so a single instance cannot sit in three pipelines as an additional handler. As the primary it is used as-is,
/// which is exactly what "one shared instance" requires (verified against .NET 10's Microsoft.Extensions.Http before choosing this).
/// </remarks>
public static class NamedHttpClientRegistration
{
    public const string FeedClientName = "feed";

    /// <summary>Frozen by T-020: <c>DiscordNotificationTarget.HttpClientName</c>.</summary>
    public const string DiscordClientName = "discord";

    public const string ImageClientName = "image";

    /// <summary>Registers the three named clients. A null section falls back to the schema defaults (30/15/15 s, no private-network blocking) —
    /// same pattern as Program's health section; boot validation already guarantees any present values are sane.</summary>
    public static IServiceCollection AddNewsQuackHttpClients(this IServiceCollection services, HttpSectionConfig? http)
    {
        ArgumentNullException.ThrowIfNull(services);

        var settings = http ?? new HttpSectionConfig();

        // One shared instance serves all three clients (stateless per request — see SsrfSafeHttpHandler). The inner handler disables automatic
        // redirects (they are followed manually, hop by hop, with validation) and enables native Gzip|Deflate decompression. The singleton
        // factory resolves the logger on first client creation — after host build, so it picks up whichever providers the host finalized
        // (JSON on stdout), not whatever happened to be registered at wiring time; null (no logging registered, e.g. bare test hosts) → NullLogger.
        services.AddSingleton(sp => new SsrfSafeHttpHandler(
            settings.BlockPrivateNetworkRedirects,
            new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate },
            sp.GetService<ILogger<SsrfSafeHttpHandler>>()));

        RegisterClient(services, FeedClientName, TimeSpan.FromSeconds(settings.FeedTimeoutSeconds));
        RegisterDiscordClient(services, TimeSpan.FromSeconds(settings.DiscordTimeoutSeconds));
        RegisterClient(services, ImageClientName, TimeSpan.FromSeconds(settings.ImageTimeoutSeconds));

        return services;
    }

    private static void RegisterClient(IServiceCollection services, string name, TimeSpan timeout) =>
        services.AddHttpClient(name, client => client.Timeout = timeout)   // per-client timeout from config seconds
                .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<SsrfSafeHttpHandler>());   // the same shared instance in every pipeline

    /// <summary>T-048: the "discord" client exactly as <see cref="RegisterClient"/>, plus the pacing handler — every request to a webhook URL waits for
    /// its per-URL pace floor before starting (both POSTs of the target's one-shot fallback swap included). The handler is an ADDITIONAL handler wrapping
    /// the shared primary (no InnerHandler of its own — the T-005 remark on primary-vs-additional applies). GetService (not GetRequired): a container
    /// without AddWebhookPacing yields a null registry ⇒ transparent pass-through, so bare test hosts keep their exact pre-T-048 pipeline.</summary>
    private static void RegisterDiscordClient(IServiceCollection services, TimeSpan timeout)
    {
        services.AddHttpClient(DiscordClientName, client => client.Timeout = timeout)   // per-client timeout from config seconds
                .AddHttpMessageHandler(sp => new WebhookPacingHandler(sp.GetService<WebhookPacerRegistry>()))   // T-048: pacing on every webhook request — null registry ⇒ pass-through
                .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<SsrfSafeHttpHandler>());   // the same shared instance in every pipeline
    }
}
