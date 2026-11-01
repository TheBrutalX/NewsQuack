using NewsQuack.App.Configuration;   // NewsQuackConfig — the registry is built from the validated config singleton (decision 5/8)
using NewsQuack.Core;   // IPostStage — the frozen contract the runner resolves (never concrete stage types)
using Microsoft.Extensions.DependencyInjection;

namespace NewsQuack.App.Pipeline;

/// <summary>
/// T-024: DI wiring for the post pipeline, shared by both host paths in Program.cs (same pattern as AddDedupEngine / AddFeedErrorTracking).
/// Registers the stateless singleton runner, the two new v1.0 stages as <see cref="IPostStage"/> (DedupCheckStage is already registered by
/// AddDedupEngine), and the per-instance send-queue registry built from the validated config. No config keys invented — the registry reads the
/// instance ids from the singleton NewsQuackConfig registered at boot.
/// </summary>
public static class PostPipelineRegistration
{
    public static IServiceCollection AddPostPipeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Stateless singleton runner — resolves IEnumerable<IPostStage> (never concrete types) and sorts by Order once at construction. The host's
        // standard logging registration supplies ILogger<PostPipelineRunner>.
        services.AddSingleton<PostPipelineRunner>();

        // The two new v1.0 stages as IPostStage. ImageResolutionStage depends on the NewsQuackConfig singleton; SendStage on the registry below.
        // Orders 20 (filters) and 40 (LLM tagging) stay free — no stage is registered there in v1.0.
        services.AddSingleton<IPostStage, ImageResolutionStage>();
        services.AddSingleton<IPostStage, SendStage>();

        // One bounded send queue per configured instance — built from the validated config at registration (decision 5). Resolved lazily so it
        // sees the NewsQuackConfig singleton registered in Program.cs after boot validation.
        services.AddSingleton(sp => new SendQueueRegistry(sp.GetRequiredService<NewsQuackConfig>()));

        return services;
    }
}
