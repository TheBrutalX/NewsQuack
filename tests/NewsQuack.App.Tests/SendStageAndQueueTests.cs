using NewsQuack.App.Configuration;   // NewsquackConfig / InstanceConfig / FeedConfig / DiscordTargetConfig — the registry is built from config
using NewsQuack.App.Pipeline;        // SendStage / BoundedSendQueue / SendQueueRegistry
using NewsQuack.Core;                // Post / PostContext — the frozen contracts
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-024 unit: SendStage (Order 100, IsolateItem, enqueues into the instance's queue via the registry) and BoundedSendQueue (bounded Channel with
/// FullMode.Wait + SingleReader). Pins per-instance isolation (no cross-instance leak), KeyNotFoundException for an unknown instance, the frozen
/// metadata, in-order round-trip on a small capacity, and that a full queue WAITS without dropping.
/// </summary>
public class SendStageAndQueueTests
{
    private const string DiscordWebhook = "https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWx";

    private static Post Post(string itemId) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ItemId = itemId,
        Title = "A title",
        PublishedAt = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero),   // always UTC
        SourceUrl = "https://example.com/feed.xml",
    };

    private static InstanceConfig Instance(string id) => new()
    {
        Id = id,
        CheckIntervalMinutes = 15,
        Feeds = [new FeedConfig { Url = $"https://example.com/{id}.xml" }],
        Discord = new DiscordTargetConfig { WebhookUrl = DiscordWebhook },
    };

    private static NewsquackConfig Config(params string[] instanceIds) => new() { Instances = instanceIds.Select(Instance).ToList() };

    [Fact]
    public async Task Send_EnqueuesCurrentPostToThisInstancesQueueOnly()
    {
        var registry = new SendQueueRegistry(Config("inst-a", "inst-b"));   // one bounded queue per configured instance
        var stage = new SendStage(registry, new LogCapture().For<SendStage>());

        var postA = Post("item-a");   // a post for instance A
        var postB = Post("item-b");   // a post for instance B
        await stage.ExecuteAsync(new PostContext { InstanceId = "inst-a", CurrentPost = postA });
        await stage.ExecuteAsync(new PostContext { InstanceId = "inst-b", CurrentPost = postB });

        // A's queue holds EXACTLY postA — no cross-instance leak...
        var queueA = (BoundedSendQueue)registry.For("inst-a");   // internal ReadAsync is test-visible (InternalsVisibleTo)
        Assert.Same(postA, await queueA.ReadAsync());
        Assert.False(queueA.TryRead(out _));

        // ...and B's queue holds EXACTLY postB.
        var queueB = (BoundedSendQueue)registry.For("inst-b");
        Assert.Same(postB, await queueB.ReadAsync());
        Assert.False(queueB.TryRead(out _));
    }

    [Fact]
    public async Task Send_UnknownInstanceId_PropagatesKeyNotFoundException()
    {
        var registry = new SendQueueRegistry(Config("inst-a"));   // only "inst-a" is configured
        var stage = new SendStage(registry, new LogCapture().For<SendStage>());

        var ctx = new PostContext { InstanceId = "inst-unknown", CurrentPost = Post("item-x") };   // an instance with no queue

        await Assert.ThrowsAsync<KeyNotFoundException>(() => stage.ExecuteAsync(ctx));   // surfaced loudly — never a silent no-op
    }

    [Fact]
    public void Send_Metadata_OrderHundredIsolateItemNamePinned()
    {
        var stage = new SendStage(new SendQueueRegistry(Config("inst-a")), new LogCapture().For<SendStage>());

        Assert.Equal(100, stage.Order);                              // the last stage — after dedup (10), filters (20), images (30), LLM (40)
        Assert.Equal("send", stage.Name);                            // stable string for structured logging
        Assert.Equal(FailurePolicy.IsolateItem, stage.FailurePolicy);   // the norm: on failure the runner drops the item (decision 4)
    }

    [Fact]
    public async Task BoundedSendQueue_EnqueueThenRead_RoundTripsInOrder()
    {
        var queue = new BoundedSendQueue(capacity: 3);   // small capacity ctor — tests never use the default 100

        var a = Post("item-a");
        var b = Post("item-b");
        var c = Post("item-c");
        await queue.EnqueueAsync(a);
        await queue.EnqueueAsync(b);
        await queue.EnqueueAsync(c);

        Assert.Same(a, await queue.ReadAsync());   // FIFO — the exact same instances come back in enqueue order...
        Assert.Same(b, await queue.ReadAsync());   // ...through the internal reader T-021's worker will use
        Assert.Same(c, await queue.ReadAsync());
    }

    [Fact]
    public async Task BoundedSendQueue_Full_WaitsUntilSpaceWithoutDropping()
    {
        var queue = new BoundedSendQueue(capacity: 1);   // a single slot — the smallest useful capacity

        var a = Post("item-a");
        await queue.EnqueueAsync(a);   // fills the only slot

        var enqueueB = queue.EnqueueAsync(Post("item-b"));   // FullMode.Wait ⇒ this WAITS for space; it is never dropped or failed
        await Task.Delay(100, CancellationToken.None);        // generous: let the write reach its wait state
        Assert.False(enqueueB.IsCompleted);                  // still waiting — proves the queue is full and blocking, not dropping

        Assert.Same(a, await queue.ReadAsync());             // free the slot...
        await enqueueB;                                      // ...now the pending write completes (no drop)...
        Assert.Equal("item-b", (await queue.ReadAsync()).ItemId);   // ...and the item round-trips intact
    }
}
