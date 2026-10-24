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
    public void DeadLetterData_AllFields_PreservesValues()   // T-022: the additive dead-letter contract record (decision 2)
    {
        var data = new DeadLetterData(
            "instance-1", "https://example.com/feed.xml", "item-9", """{"ItemId":"item-9"}""", 5, "429", "Discord rate limit", FixedUtc);

        Assert.Equal("instance-1", data.InstanceId);
        Assert.Equal("https://example.com/feed.xml", data.FeedUrl);
        Assert.Equal("item-9", data.ItemId);
        Assert.Equal("""{"ItemId":"item-9"}""", data.PayloadJson);   // the full post payload as JSON — carried verbatim to the store
        Assert.Equal(5, data.Attempts);
        Assert.Equal("429", data.ErrorCategory);                     // one of "429" / "5xx" / "timeout" / "400-payload" (entity taxonomy)
        Assert.Equal("Discord rate limit", data.LastError);
        Assert.Equal(FixedUtc, data.FailedAtUtc);                    // UTC only — the store converts at the boundary
    }

    [Fact]
    public void DeadLetterData_NullLastError_RemainsNull()   // T-022: a dead letter may carry no error string (the classifier still buckets it)
    {
        var data = new DeadLetterData("instance-1", "https://example.com/feed.xml", "item-9", "{}", 5, "5xx", null, FixedUtc);

        Assert.Null(data.LastError);
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
