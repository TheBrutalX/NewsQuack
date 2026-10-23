using Xunit;

namespace NewsQuack.Core.Tests;

public class PostTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Post_DefaultTags_IsEmptyListNotNull()
    {
        var post = CreatePostWithoutOptionalFields();

        Assert.NotNull(post.Tags);
        Assert.Empty(post.Tags);
    }

    [Fact]
    public void Post_NullableFields_OmittedDescriptionAndLink_AreNullWithoutError()
    {
        var post = CreatePostWithoutOptionalFields();

        Assert.Null(post.Description);
        Assert.Null(post.Link);
        Assert.Null(post.Author);
        Assert.Null(post.ImageUrl);
    }

    private static Post CreatePostWithoutOptionalFields() => new()
    {
        Id = Guid.NewGuid().ToString(),
        ItemId = "item-1",
        Title = "Title",
        PublishedAt = FixedUtc,
        SourceUrl = "https://example.com/feed.xml",
    };
}
