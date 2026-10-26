using System.Text.Json;
using NewsQuack.App.Configuration;
using NewsQuack.App.Discord;
using NewsQuack.Core;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-020 payload tests: the builder is called directly (no HTTP). Limits are exact — an over-limit field must never be emitted.</summary>
public class DiscordPayloadBuilderTests
{
    private static readonly DateTimeOffset PublishedAt = new(2026, 10, 23, 14, 5, 9, TimeSpan.Zero);   // renders as "2026-10-23T14:05:09Z"

    private static Post MakePost(string title = "Hello", string? description = null, string? link = "https://example.com/item", string? imageUrl = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ItemId = "item-1",
        Title = title,
        Description = description,
        Link = link,
        Author = null,
        PublishedAt = PublishedAt,
        ImageUrl = imageUrl,
        SourceUrl = "https://example.com/feed.xml",   // host: example.com — the forum fallback value under test
    };

    private static DiscordTargetConfig MakeConfig(string mode = "embed", int? color = null, string? fallbackImage = null) => new()
    {
        WebhookUrl = ConfigTestHelper.WebhookUrl,
        Mode = mode,
        Color = color,
        FallbackImageUrl = fallbackImage,
    };

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Payload_Embed_TitleOver256_TruncatedWithEllipsisAtExactly256()
    {
        var post = MakePost(title: new string('t', 300));

        var payload = Parse(DiscordPayloadBuilder.BuildPayload(post, MakeConfig(), DiscordMode.Embed));
        var title = payload.GetProperty("embeds")[0].GetProperty("title").GetString()!;

        Assert.Equal(256, title.Length);                     // exactly the limit — never over
        Assert.EndsWith("…", title);                         // single trailing ellipsis when cutting
        Assert.Equal(new string('t', 255), title[..255]);    // …and nothing else was altered
    }

    [Fact]
    public void Payload_Embed_DescriptionOver4096_TruncatedAtExactly4096()
    {
        var post = MakePost(description: new string('x', 5000));

        var payload = Parse(DiscordPayloadBuilder.BuildPayload(post, MakeConfig(), DiscordMode.Embed));
        var description = payload.GetProperty("embeds")[0].GetProperty("description").GetString()!;

        Assert.Equal(4096, description.Length);
        Assert.EndsWith("…", description);
        Assert.Equal(new string('x', 4095), description[..4095]);
    }

    [Fact]
    public void Payload_Forum_ThreadNameOver100_TruncatedAtExactly100()
    {
        var post = MakePost(title: new string('t', 150));

        var payload = Parse(DiscordPayloadBuilder.BuildPayload(post, MakeConfig("forum"), DiscordMode.Forum));
        var threadName = payload.GetProperty("thread_name").GetString()!;

        Assert.Equal(100, threadName.Length);
        Assert.EndsWith("…", threadName);
    }

    [Fact]
    public void Payload_Forum_EmptyTitle_FallsBackToSourceUrlHost()
    {
        // The v1.0 "feed name" fallback: null/empty title → host of post.SourceUrl (documented in the T-020 report).
        foreach (var emptyTitle in new[] { string.Empty, "   " })
        {
            var payload = Parse(DiscordPayloadBuilder.BuildPayload(MakePost(title: emptyTitle), MakeConfig("forum"), DiscordMode.Forum));

            Assert.Equal("example.com", payload.GetProperty("thread_name").GetString());
        }
    }

    [Fact]
    public void Payload_Forum_EmbedIdenticalToEmbedMode()
    {
        var post = MakePost(description: "<p>Body &amp; more</p>", imageUrl: "https://img.example.com/p.png");

        var embedPayload = Parse(DiscordPayloadBuilder.BuildPayload(post, MakeConfig("embed", color: 0x112233), DiscordMode.Embed));
        var forumPayload = Parse(DiscordPayloadBuilder.BuildPayload(post, MakeConfig("forum", color: 0x112233), DiscordMode.Forum));

        Assert.Equal(embedPayload.GetProperty("embeds").GetRawText(), forumPayload.GetProperty("embeds").GetRawText());   // same embed, plus thread_name at top level
    }

    [Fact]
    public void Payload_AllModes_HtmlDescription_StrippedToPlainTextCoherently()
    {
        // Tags + common entities + decimal and hex numeric refs — one shared helper, so all three modes must yield the same text.
        const string html = "<p>Hello &amp; welcome</p><br/><b>bold</b> &quot;quoted&quot; &#39;single&#39; &#60;tag&#62;&#x2F;";
        const string expectedPlain = "Hello & welcome bold \"quoted\" 'single' <tag>/";

        var post = MakePost(title: "Html Post", description: html);
        var config = MakeConfig();

        var embedText = Parse(DiscordPayloadBuilder.BuildPayload(post, config, DiscordMode.Embed))
            .GetProperty("embeds")[0].GetProperty("description").GetString()!;
        var forumText = Parse(DiscordPayloadBuilder.BuildPayload(post, config, DiscordMode.Forum))
            .GetProperty("embeds")[0].GetProperty("description").GetString()!;
        var markdownContent = Parse(DiscordPayloadBuilder.BuildPayload(post, config, DiscordMode.Markdown))
            .GetProperty("content").GetString()!;

        Assert.Equal(expectedPlain, embedText);   // tags gone, entities decoded (incl. &#60; → < and &#x2F; → /), whitespace collapsed
        Assert.Equal(embedText, forumText);       // coherent across modes…
        Assert.DoesNotContain("<p>", markdownContent);   // …and no raw markup leaks into the markdown template
        Assert.Contains(expectedPlain, markdownContent);

        // An HTML description that strips to nothing behaves like "no description" in every mode.
        var emptyPost = MakePost(description: "<b></b>");

        Assert.Null(DiscordPayloadBuilder.PlainText(emptyPost.Description));
        Assert.False(Parse(DiscordPayloadBuilder.BuildPayload(emptyPost, config, DiscordMode.Embed)).GetProperty("embeds")[0].TryGetProperty("description", out _));
    }

    [Fact]
    public void Payload_Embed_ImageNull_UsesFallbackImageFromConfig()
    {
        const string fallback = "https://img.example.com/fallback.png";

        // Both a null ImageUrl and an empty one (image resolution failed earlier in the pipeline) must fall back.
        foreach (var imageUrl in new[] { null, string.Empty })
        {
            var payload = Parse(DiscordPayloadBuilder.BuildPayload(MakePost(imageUrl: imageUrl), MakeConfig(fallbackImage: fallback), DiscordMode.Embed));

            Assert.Equal(fallback, payload.GetProperty("embeds")[0].GetProperty("image").GetProperty("url").GetString());
        }

        // …but the post's own image always wins when present.
        var withOwn = Parse(DiscordPayloadBuilder.BuildPayload(MakePost(imageUrl: "https://img.example.com/own.png"), MakeConfig(fallbackImage: fallback), DiscordMode.Embed));
        Assert.Equal("https://img.example.com/own.png", withOwn.GetProperty("embeds")[0].GetProperty("image").GetProperty("url").GetString());
    }

    [Fact]
    public void Payload_Embed_NoImageAtAll_OmitsImageField()
    {
        var payload = Parse(DiscordPayloadBuilder.BuildPayload(MakePost(), MakeConfig(), DiscordMode.Embed));

        Assert.False(payload.GetProperty("embeds")[0].TryGetProperty("image", out _));   // omitted — not null, not an empty object
    }

    [Fact]
    public void Payload_Markdown_TemplateExactWithOptionalPartsOmitted()
    {
        // Null link + empty description + no image: only the bold title remains — plus the always-present T-052 feed section.
        var bare = Parse(DiscordPayloadBuilder.BuildPayload(MakePost(title: "My Title", link: null, description: null), MakeConfig(), DiscordMode.Markdown));
        Assert.Equal("**My Title**\n\nFeed: https://example.com/feed.xml", bare.GetProperty("content").GetString());

        // All parts present: the amended template (T-020 frozen, T-052 amendment), byte for byte.
        var full = Parse(DiscordPayloadBuilder.BuildPayload(
            MakePost(title: "My Title", description: "<p>Plain &amp; simple</p>", imageUrl: "https://img.example.com/post.png"),
            MakeConfig(), DiscordMode.Markdown));

        Assert.Equal("**My Title**\nhttps://example.com/item\n\nPlain & simple\n\nhttps://img.example.com/post.png\n\nFeed: https://example.com/feed.xml", full.GetProperty("content").GetString());
    }

    [Fact]
    public void BuildEmbedPayload_PostWithSourceUrl_FooterContainsFeedLabelAndUrl()
    {
        var payload = Parse(DiscordPayloadBuilder.BuildPayload(MakePost(), MakeConfig(), DiscordMode.Embed));

        Assert.Equal("Feed: https://example.com/feed.xml", payload.GetProperty("embeds")[0].GetProperty("footer").GetProperty("text").GetString());   // byte-exact — the default SourceUrl
    }

    [Fact]
    public void BuildForumPayload_SamePost_FooterIdenticalToEmbedMode()
    {
        var post = MakePost(description: "<p>Body &amp; more</p>", imageUrl: "https://img.example.com/p.png");

        var embedFooter = Parse(DiscordPayloadBuilder.BuildPayload(post, MakeConfig("embed", color: 0x112233), DiscordMode.Embed))
            .GetProperty("embeds")[0].GetProperty("footer").GetRawText();
        var forumFooter = Parse(DiscordPayloadBuilder.BuildPayload(post, MakeConfig("forum", color: 0x112233), DiscordMode.Forum))
            .GetProperty("embeds")[0].GetProperty("footer").GetRawText();

        Assert.Equal(embedFooter, forumFooter);   // shared BuildEmbedObject — the footer is byte-identical in both modes
    }

    [Fact]
    public void BuildMarkdownPayload_PostWithSourceUrl_ContentEndsWithFeedSection()
    {
        const string feedSection = "\n\nFeed: https://example.com/feed.xml";

        // Bare post: the feed section lands right after the title.
        var bare = Parse(DiscordPayloadBuilder.BuildPayload(MakePost(link: null, description: null), MakeConfig(), DiscordMode.Markdown));
        Assert.EndsWith(feedSection, bare.GetProperty("content").GetString()!);

        // All parts present: the feed section is still the final part of the content.
        var full = Parse(DiscordPayloadBuilder.BuildPayload(
            MakePost(description: "<p>Plain &amp; simple</p>", imageUrl: "https://img.example.com/post.png"),
            MakeConfig(), DiscordMode.Markdown));
        Assert.EndsWith(feedSection, full.GetProperty("content").GetString()!);
    }

    [Fact]
    public void BuildEmbedPayload_SourceUrlOver2048_FooterTruncatedAtExactly2048()
    {
        var post = MakePost() with { SourceUrl = new string('u', 2100) };   // "Feed: " + 2100 chars > the 2048 footer limit

        var footer = Parse(DiscordPayloadBuilder.BuildPayload(post, MakeConfig(), DiscordMode.Embed))
            .GetProperty("embeds")[0].GetProperty("footer").GetProperty("text").GetString()!;

        Assert.Equal(2048, footer.Length);                       // exactly the limit — never over (Discord would reject the whole embed)
        Assert.EndsWith("…", footer);                            // single trailing ellipsis when cutting
        Assert.Equal("Feed: " + new string('u', 2041), footer[..2047]);   // …and nothing else was altered
    }

    [Fact]
    public void BuildEmbedPayload_NoDescriptionLinkOrImage_FooterStillPresent()
    {
        var embed = Parse(DiscordPayloadBuilder.BuildPayload(MakePost(link: null, description: null), MakeConfig(), DiscordMode.Embed))
            .GetProperty("embeds")[0];

        Assert.True(embed.TryGetProperty("footer", out _));      // unconditional — unlike every other optional field in this method
        Assert.Equal("Feed: https://example.com/feed.xml", embed.GetProperty("footer").GetProperty("text").GetString());
    }

    [Fact]
    public void Payload_Embed_AuthorNull_OmitsAuthorField()
    {
        var payload = Parse(DiscordPayloadBuilder.BuildPayload(MakePost(), MakeConfig(), DiscordMode.Embed));

        Assert.False(payload.GetProperty("embeds")[0].TryGetProperty("author", out _));
    }

    [Fact]
    public void Payload_Embed_AuthorFieldsOverLimit_TruncatedAt512And1024()
    {
        var post = MakePost();
        var withAuthor = post with { Author = new PostAuthor(new string('a', 600), new string('u', 1100)) };

        var author = Parse(DiscordPayloadBuilder.BuildPayload(withAuthor, MakeConfig(), DiscordMode.Embed)).GetProperty("embeds")[0].GetProperty("author");

        Assert.Equal(512, author.GetProperty("name").GetString()!.Length);
        Assert.EndsWith("…", author.GetProperty("name").GetString());
        Assert.Equal(1024, author.GetProperty("url").GetString()!.Length);
        Assert.EndsWith("…", author.GetProperty("url").GetString());
    }

    [Fact]
    public void Payload_Embed_Timestamp_Iso8601UtcZ()
    {
        var payload = Parse(DiscordPayloadBuilder.BuildPayload(MakePost(), MakeConfig(), DiscordMode.Embed));

        Assert.Equal("2026-10-23T14:05:09Z", payload.GetProperty("embeds")[0].GetProperty("timestamp").GetString());
    }

    [Fact]
    public void Payload_Embed_OptionalFieldsOmittedWhenAbsent()
    {
        var embed = Parse(DiscordPayloadBuilder.BuildPayload(MakePost(link: null), MakeConfig(), DiscordMode.Embed)).GetProperty("embeds")[0];

        Assert.False(embed.TryGetProperty("url", out _));       // link null → omitted
        Assert.False(embed.TryGetProperty("color", out _));     // color null → omitted
    }

    [Theory]
    [InlineData(null, DiscordMode.Embed)]      // schema default when the key is omitted
    [InlineData("embed", DiscordMode.Embed)]
    [InlineData("FORUM", DiscordMode.Forum)]   // case-insensitive per the config docs
    [InlineData("Markdown", DiscordMode.Markdown)]
    public void ParseMode_Values_MapCaseInsensitively(string? mode, DiscordMode expected) =>
        Assert.Equal(expected, DiscordPayloadBuilder.ParseMode(mode));

    [Fact]
    public void TruncateWithEllipsis_Fits_ReturnsUnchanged()
    {
        Assert.Equal("short", DiscordPayloadBuilder.TruncateWithEllipsis("short", 256));
        Assert.Equal(new string('t', 256), DiscordPayloadBuilder.TruncateWithEllipsis(new string('t', 256), 256));   // exactly the limit — no ellipsis
    }

    [Fact]
    public void TruncateWithEllipsis_SingleTrailingEllipsis_EvenWhenSourceEndsWithOne()
    {
        var value = new string('a', 10) + "…";

        var truncated = DiscordPayloadBuilder.TruncateWithEllipsis(value, 5);

        Assert.Equal(5, truncated.Length);
        Assert.Equal("aaaa…", truncated);   // exactly one trailing ellipsis — not doubled (4 source chars + …)
    }
}
