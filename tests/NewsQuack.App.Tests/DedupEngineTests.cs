using NewsQuack.App.Pipeline;
using NewsQuack.Core;
using Microsoft.Extensions.DependencyInjection;   // wiring test only — the exact AddDedupEngine call Program.cs makes
using Microsoft.Extensions.Logging;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-014 unit: DedupEngine against a recording in-memory IStateStore fake (no SQLite). Pins the thin delegation (exact argument pass-through,
/// both true and false answers, no caching layer) and the DI wiring. The at-least-once round trips live in DedupAtLeastOnceTests; the stage
/// behavior lives in DedupCheckStageTests.
/// </summary>
public class DedupEngineTests
{
    private const string ItemId = "item-1";

    /// <summary>The fixed injected instant — never a real clock (AGENTS.md UTC rule).</summary>
    private static readonly DateTimeOffset DeliveredAtUtc = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsDelivered_DelegatesToStoreWithInstanceIdAndItemId_ReturnsStoredValue(bool stored)
    {
        var store = new RecordingStateStore();
        if (stored)
            store.SeedDelivered(DedupTestPost.InstanceId, ItemId);

        var engine = new DedupEngine(store, new LogCapture().For<DedupEngine>());

        var delivered = await engine.IsDeliveredAsync(DedupTestPost.InstanceId, ItemId);

        Assert.Equal(stored, delivered);
        // Exactly one lookup with exactly the arguments the caller passed — no caching layer in front (T-014 decision 1).
        Assert.Single(store.IsDeliveredCalls);
        Assert.Equal((DedupTestPost.InstanceId, ItemId), store.IsDeliveredCalls[0]);
    }

    [Fact]
    public async Task ConfirmDelivery_PassesAllArgumentsThroughUnchanged()
    {
        var store = new RecordingStateStore();
        var engine = new DedupEngine(store, new LogCapture().For<DedupEngine>());
        var newState = new FeedState(
            DedupTestPost.FeedUrl,
            DeliveredAtUtc.AddHours(-1),   // LastItemPublishedAtUtc — a distinct value so a copy/rebuild would be visible
            "W/\"v42\"",                   // Etag
            DeliveredAtUtc,                // LastFetchedAtUtc
            ErrorCount: 3);

        await engine.ConfirmDeliveryAsync(DedupTestPost.InstanceId, DedupTestPost.FeedUrl, ItemId, DeliveredAtUtc, newState);

        Assert.Single(store.MarkDeliveredCalls);
        var call = store.MarkDeliveredCalls[0];
        Assert.Equal(DedupTestPost.InstanceId, call.InstanceId);
        Assert.Equal(DedupTestPost.FeedUrl, call.FeedUrl);
        Assert.Equal(ItemId, call.ItemId);
        Assert.Equal(DeliveredAtUtc, call.DeliveredAtUtc);
        // The FeedState record passes through BY REFERENCE — the engine never copies or rebuilds it (T-014 decision 1).
        Assert.Same(newState, call.NewFeedState);
    }

    [Fact]
    public async Task ConfirmDelivery_LogsInfoWithInstanceIdFeedUrlAndItemId()
    {
        var store = new RecordingStateStore();
        var logs = new LogCapture();
        var engine = new DedupEngine(store, logs.For<DedupEngine>());

        await engine.ConfirmDeliveryAsync(DedupTestPost.InstanceId, DedupTestPost.FeedUrl, ItemId, DeliveredAtUtc, null);

        // The observable "delivered" moment (T-014 decision 1) — Info with all three identifiers in the structured template.
        var entry = logs.Find(LogLevel.Information, DedupTestPost.InstanceId, DedupTestPost.FeedUrl, ItemId);
        Assert.NotNull(entry);
    }

    // ── DI wiring (decision 1) ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddDedupEngine_WiredAsInProgram_ResolvesSingletonsEndToEnd()
    {
        var store = new RecordingStateStore();
        var logs = new LogCapture();

        // The exact wiring Program.cs uses on both host paths — the fake IStateStore stands in for SqliteStateStore (the engine only sees the contract).
        var services = new ServiceCollection();
        services.AddSingleton<IStateStore>(store);
        services.AddSingleton<ILogger<DedupEngine>>(logs.For<DedupEngine>());
        services.AddSingleton<ILogger<DedupCheckStage>>(logs.For<DedupCheckStage>());
        services.AddDedupEngine();

        using var provider = services.BuildServiceProvider();

        // Later tasks (send worker / pipeline runner) resolve the interfaces — never the concrete types.
        var engine = provider.GetRequiredService<IDedupEngine>();
        Assert.IsType<DedupEngine>(engine);
        var stage = provider.GetRequiredService<IPostStage>();
        Assert.IsType<DedupCheckStage>(stage);

        // Both stateless singletons — the same instances come back on every resolve (one per process, serving all instances/feeds).
        Assert.Same(engine, provider.GetRequiredService<IDedupEngine>());
        Assert.Same(stage, provider.GetRequiredService<IPostStage>());

        // End-to-end through the container: a confirm via the resolved engine makes the resolved stage skip the item for that instance.
        await engine.ConfirmDeliveryAsync(DedupTestPost.InstanceId, DedupTestPost.FeedUrl, ItemId, DeliveredAtUtc, null);

        var ctx = DedupTestPost.Context(DedupTestPost.InstanceId, ItemId);
        await stage.ExecuteAsync(ctx);

        Assert.True(ctx.Skip);
    }
}
