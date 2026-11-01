using NewsQuack.App.Configuration;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-003: webhook token masking — the last path segment of a Discord webhook URL is the secret and must never appear in full in output.</summary>
public class WebhookMaskerTests
{
    [Fact]
    public void Masker_DiscordWebhookUrl_KeepsHostIdAndLastFourTokenChars()
    {
        // realistic URL (id + 24-char token) → scheme+host+/api/webhooks/<id>/ kept, only the last 4 token chars visible
        Assert.Equal(
            "https://discord.com/api/webhooks/123456789012345678/…UvWx",
            WebhookMasker.Mask(ConfigTestHelper.WebhookUrl));

        // wrong scheme still gets masked (defense in depth at echo sites of malformed values)
        Assert.Equal(
            "http://discord.com/api/webhooks/123456789012345678/…UvWx",
            WebhookMasker.Mask("http://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWx"));

        // discordapp host variant (last 4 chars of "someTokenAbCd" are "AbCd")
        Assert.Equal(
            "https://discordapp.com/api/webhooks/42/…AbCd",
            WebhookMasker.Mask("https://discordapp.com/api/webhooks/42/someTokenAbCd"));

        // token shorter than 4 characters → the whole (short) tail is shown, nothing more
        Assert.Equal(
            "https://discord.com/api/webhooks/7/…tok",
            WebhookMasker.Mask("https://discord.com/api/webhooks/7/tok"));
    }

    [Theory]
    [InlineData("https://example.com/feed.xml")]   // feed URL — not a secret, print as-is
    [InlineData("https://images.example.com/pic.png")]
    [InlineData("/relative/path.xml")]             // relative URL
    [InlineData("not a url at all")]
    [InlineData("")]                               // empty string passes through unchanged
    public void Masker_NonDiscordUrl_PassesThroughUnchanged(string url) =>
        Assert.Equal(url, WebhookMasker.Mask(url));

    [Fact]
    public void Masker_NullInput_ReturnsNull() =>
        Assert.Null(WebhookMasker.Mask(null));

    [Fact]
    public void Validation_ErrorEchoingWebhookUrl_IsMaskedInMessage()
    {
        // bad-shape webhook from the file (wrong scheme) → the error echoes only the masked form, never the full token
        const string webhook = "http://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWx";
        var discord = $$"""{ "WebhookUrl": "{{webhook}}" }""";
        var json = ConfigTestHelper.Config(ConfigTestHelper.Instance("a", 15, discord: discord));

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.False(ok);
        Assert.Single(messages);
        var message = messages[0];
        Assert.Contains("Instances[0].Discord.WebhookUrl", message);
        Assert.Contains("…UvWx", message);                                  // masked form present
        Assert.DoesNotContain("AbCdEfGhIjKlMnOpQrStUvWx", message);         // full token absent
    }

    [Fact]
    public void Validation_ErrorEchoingFeedUrl_IsNotMasked()
    {
        // feed URLs are not secrets — they stay as-is in error messages (only webhook tokens are masked)
        var json = ConfigTestHelper.Config(ConfigTestHelper.Instance("a", 15, feedUrls: ["ftp://secret.example.com/feed.xml"]));

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.False(ok);
        Assert.Single(messages);
        Assert.Contains("ftp://secret.example.com/feed.xml", messages[0]);
    }
}
