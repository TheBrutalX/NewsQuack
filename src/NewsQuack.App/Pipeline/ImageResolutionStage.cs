using NewsQuack.App.Configuration;   // NewsquackConfig — the validated, immutable config (Discord FallbackImageUrl lookup)
using NewsQuack.Core;
using Microsoft.Extensions.Logging;   // ILogger<T>

namespace NewsQuack.App.Pipeline;

/// <summary>
/// T-024: minimal v1.0 image-resolution stage (coordinator decision 3). XML images are ALREADY resolved at parse time by T-012/T-013
/// (<c>FeedItemMapper</c> + the YouTube thumbnail preference) — this stage only fills the gap: when <see cref="Post.ImageUrl"/> is null/empty it
/// replaces the post via <c>with</c> using THIS instance's Discord <c>FallbackImageUrl</c> (looked up from the validated config by
/// <see cref="PostContext.InstanceId"/>). If no fallback is configured it leaves the value null and logs Debug. No I/O in v1.0 (page scraping is
/// an M2 rework), so a failure is effectively impossible — but the <see cref="FailurePolicy.ContinueDegraded"/> policy pre-declares that an image
/// is enrichment: a failure must degrade the post, never drop it. Stateless singleton.
/// </summary>
public sealed class ImageResolutionStage : IPostStage
{
    private readonly NewsquackConfig _config;   // validated at boot — immutable init-only object, safe to share as a singleton dependency
    private readonly ILogger<ImageResolutionStage> _logger;

    public ImageResolutionStage(NewsquackConfig config, ILogger<ImageResolutionStage> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _logger = logger;
    }

    /// <summary>Order 30 — after dedup (10) and the reserved filters slot (20), before the reserved LLM slot (40) and send (100).</summary>
    public int Order => 30;

    /// <summary>Stable name for structured logging.</summary>
    public string Name => "image-resolution";

    /// <summary>Image is enrichment — a failure degrades the post (ContinueDegraded), it never drops the item (decision 3).</summary>
    public FailurePolicy FailurePolicy => FailurePolicy.ContinueDegraded;

    /// <summary>No I/O in v1.0, so this runs synchronously and returns a completed task — no async keyword (avoids CS1998 under -warnaserror).</summary>
    public Task ExecuteAsync(PostContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ct.ThrowIfCancellationRequested();   // honor the token end-to-end even though there is no I/O yet (M2 page scraping will await it)

        if (!string.IsNullOrEmpty(ctx.CurrentPost.ImageUrl))
            return Task.CompletedTask;   // already resolved at parse time — nothing to do, post stays untouched

        var fallback = ResolveFallbackImageUrl(ctx.InstanceId);

        if (fallback is null)
        {
            _logger.LogDebug("No image and no configured Discord fallback for instance {InstanceId} — leaving ImageUrl null: item {ItemId}",
                ctx.InstanceId, ctx.CurrentPost.ItemId);
            return Task.CompletedTask;   // no fallback to apply — leave null, never invent one
        }

        // Replace via 'with' (never mutate in place): a NEW Post instance with every other field preserved (decision 3).
        ctx.CurrentPost = ctx.CurrentPost with { ImageUrl = fallback };

        return Task.CompletedTask;
    }

    /// <summary>This instance's Discord FallbackImageUrl from the validated config — null when the instance, its Discord section or the
    /// fallback itself is absent. Boot validation guarantees Ids are unique and non-empty, so the lookup is unambiguous.</summary>
    private string? ResolveFallbackImageUrl(string instanceId) =>
        _config.Instances?.FirstOrDefault(instance => instance.Id == instanceId)?.Discord?.FallbackImageUrl;
}
