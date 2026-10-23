using Xunit;

namespace NewsQuack.Core.Tests;

/// <summary>Contract behavior of the frozen POCO records and classes (T-001): construction, defaults, read-back.</summary>
public class ContractRecordsTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PostAuthor_ConstructionWithAllFields_PreservesValues()
    {
        var author = new PostAuthor("Jane Doe", "https://example.org/jane");

        Assert.Equal("Jane Doe", author.Name);
        Assert.Equal("https://example.org/jane", author.Url);
    }

    [Fact]
    public void PostAuthor_NullUrl_RemainsNull()
    {
        var author = new PostAuthor("Jane Doe", null);

        Assert.Null(author.Url);
    }

    [Fact]
    public void FeedParseResult_PostsErrorsAndFeedError_PreservesValues()
    {
        var result = new FeedParseResult(Array.Empty<Post>(), 2, "feed-level error");

        Assert.Empty(result.Posts);
        Assert.Equal(2, result.ItemErrorCount);
        Assert.Equal("feed-level error", result.FeedError);
    }

    [Fact]
    public void FeedState_AllFields_PreservesValues()
    {
        var state = new FeedState(
            "https://example.com/feed.xml", FixedUtc, "etag-1", FixedUtc.AddHours(-1), 3, FixedUtc.AddMinutes(-5));

        Assert.Equal("https://example.com/feed.xml", state.FeedUrl);
        Assert.Equal(FixedUtc, state.LastItemPublishedAtUtc);
        Assert.Equal("etag-1", state.Etag);
        Assert.Equal(FixedUtc.AddHours(-1), state.LastFetchedAtUtc);
        Assert.Equal(3, state.ErrorCount);
        Assert.Equal(FixedUtc.AddMinutes(-5), state.LastErrorAtUtc);
    }

    [Fact]
    public void FeedState_OmittedOptionalFields_GetDocumentedDefaults()
    {
        var state = new FeedState("https://example.com/feed.xml", null, null, null);

        Assert.Null(state.LastItemPublishedAtUtc);
        Assert.Null(state.Etag);
        Assert.Null(state.LastFetchedAtUtc);
        Assert.Equal(0, state.ErrorCount);
        Assert.Null(state.LastErrorAtUtc);
    }

    [Fact]
    public void PostContext_SkipAndPostReplacement_UpdateState()
    {
        var post = new Post
        {
            Id = "1", ItemId = "item-1", Title = "original", PublishedAt = FixedUtc, SourceUrl = "https://example.com/feed.xml",
        };

        var ctx = new PostContext { InstanceId = "instance-1", CurrentPost = post };
        Assert.False(ctx.Skip);

        ctx.Skip = true;
        ctx.CurrentPost = post with { Title = "replaced" };

        Assert.True(ctx.Skip);
        Assert.Equal("instance-1", ctx.InstanceId);
        Assert.Equal("replaced", ctx.CurrentPost.Title);
    }
}
