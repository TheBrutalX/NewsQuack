using NewsQuack.Core;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-014: shared fixtures for the dedup tests — one minimal valid <see cref="Post"/> and fixed instance/feed identifiers, so every test builds
/// its context the same way (UTC instant per AGENTS.md; nullable fields left null — a valid item may have no link/author/description).
/// </summary>
internal static class DedupTestPost
{
    public const string InstanceId = "inst-a";
    public const string OtherInstanceId = "inst-b";   // decision 2: the per-instance dimension is load-bearing — pinned with a different instance id
    public const string FeedUrl = "https://example.com/feed.xml";

    /// <summary>A minimal valid Post with the given itemId — every other field at a fixed, valid value.</summary>
    public static Post Create(string itemId) => new()
    {
        Id = Guid.NewGuid().ToString(),   // system-assigned unique post id (GUID string), as the builders assign it
        ItemId = itemId,                  // the stable dedup key — what T-012's derivation produces; consumed as-is
        Title = "A title",
        PublishedAt = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero),   // always UTC
        SourceUrl = FeedUrl,
    };

    /// <summary>A real PostContext for the given instance + item — exactly what the pipeline runner would build for one parsed item.</summary>
    public static PostContext Context(string instanceId, string itemId) => new()
    {
        InstanceId = instanceId,
        CurrentPost = Create(itemId),
    };
}
