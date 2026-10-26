using NewsQuack.App.Configuration;   // NewsQuackConfig / InstanceConfig / FeedConfig / DiscordTargetConfig — the fallback-image source
using NewsQuack.App.Pipeline;        // ImageResolutionStage
using NewsQuack.Core;                // Post / PostContext — the frozen contracts
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-024 unit: ImageResolutionStage (minimal v1.0, coordinator decision 3). Pins that a parse-time-resolved image is left untouched, a missing
/// image is filled with THIS instance's Discord FallbackImageUrl via a <c>with</c> replacement (new instance, other fields preserved), an absent
/// fallback leaves the value null, and the frozen metadata (Order 30 / "image-resolution" / ContinueDegraded). No I/O in v1.0.
/// </summary>
public class ImageResolutionStageTests
{
    private const string InstanceId = "inst-a";
    private const string ItemId = "item-1";

    private static Post Post(string? imageUrl) => new()
    {
        Id = Guid.NewGuid().ToString(),   // system-assigned unique post id (GUID string), as the builders assign it
        ItemId = ItemId,                  // the stable dedup key
        Title = "A title",
        PublishedAt = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero),   // always UTC
        SourceUrl = "https://example.com/feed.xml",
        ImageUrl = imageUrl,              // null when the item has no image of its own
    };

    private static PostContext Context(string? imageUrl) => new() { InstanceId = InstanceId, CurrentPost = Post(imageUrl) };

    private static NewsQuackConfig Config(string instanceId, string? fallbackImageUrl) => new()
    {
        Instances = [new InstanceConfig
        {
            Id = instanceId,
            CheckIntervalMinutes = 15,
            Feeds = [new FeedConfig { Url = "https://example.com/feed.xml" }],
            Discord = new DiscordTargetConfig { WebhookUrl = "https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWx", FallbackImageUrl = fallbackImageUrl },
        }],
    };

    private static ImageResolutionStage Stage(NewsQuackConfig config) => new(config, new LogCapture().For<ImageResolutionStage>());

    [Fact]
    public async Task Image_ResolvedAtParseTime_PostUntouched()
    {
        var stage = Stage(Config(InstanceId, "https://img.example/fallback.png"));
        var ctx = Context("https://img.example/own-image.png");   // an image already resolved at parse time
        var original = ctx.CurrentPost;

        await stage.ExecuteAsync(ctx);

        Assert.Same(original, ctx.CurrentPost);   // never replaced...
        Assert.Equal("https://img.example/own-image.png", ctx.CurrentPost.ImageUrl);   // ...and the parse-time image is preserved
    }

    [Fact]
    public async Task Image_MissingAndFallbackConfigured_PostReplacedWithFallbackViaWithExpression()
    {
        const string fallback = "https://img.example/fallback.png";
        var stage = Stage(Config(InstanceId, fallback));
        var ctx = Context(null);   // no image of its own — the gap this stage fills
        var original = ctx.CurrentPost;

        await stage.ExecuteAsync(ctx);

        Assert.NotSame(original, ctx.CurrentPost);   // replaced via 'with' — a NEW instance...
        Assert.Equal(fallback, ctx.CurrentPost.ImageUrl);   // ...carrying the instance's Discord FallbackImageUrl...
        // Every other field is preserved by the 'with' expression (decision 3):
        Assert.Equal(original.Id, ctx.CurrentPost.Id);
        Assert.Equal(original.ItemId, ctx.CurrentPost.ItemId);
        Assert.Equal(original.Title, ctx.CurrentPost.Title);
        Assert.Equal(original.PublishedAt, ctx.CurrentPost.PublishedAt);
        Assert.Equal(original.SourceUrl, ctx.CurrentPost.SourceUrl);
    }

    [Fact]
    public async Task Image_MissingAndNoFallback_StaysNull()
    {
        var stage = Stage(Config(InstanceId, fallbackImageUrl: null));   // the instance has no Discord FallbackImageUrl
        var ctx = Context(null);

        await stage.ExecuteAsync(ctx);

        Assert.Null(ctx.CurrentPost.ImageUrl);   // nothing to apply — leave null, never invent an image...
        Assert.Same(ctx.CurrentPost, ctx.CurrentPost);   // ...and the post is left untouched (no replacement)
    }

    [Fact]
    public async Task Image_MissingAndUnknownInstance_StaysNull()
    {
        var stage = Stage(Config(InstanceId, "https://img.example/fallback.png"));   // config knows only "inst-a"
        var ctx = new PostContext { InstanceId = "inst-unknown", CurrentPost = Post(null) };   // a post for an instance not in the config

        await stage.ExecuteAsync(ctx);

        Assert.Null(ctx.CurrentPost.ImageUrl);   // no matching instance ⇒ no fallback ⇒ stays null (never another instance's image)
    }

    [Fact]
    public void Image_Metadata_OrderThirtyContinueDegradedNamePinned()
    {
        var stage = Stage(Config(InstanceId, "https://img.example/fallback.png"));

        Assert.Equal(30, stage.Order);                              // after dedup (10) + filters (20), before LLM (40) and send (100)
        Assert.Equal("image-resolution", stage.Name);               // stable string for structured logging
        Assert.Equal(FailurePolicy.ContinueDegraded, stage.FailurePolicy);   // image is enrichment — degrade, never drop (decision 3)
    }
}
