using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-002: total config validation — every rule from the frozen v2 schema, all errors collected in one pass.</summary>
public class ConfigValidationTests
{
    [Fact]
    public void ConfigLoader_ValidFile_ReturnsModelWithoutErrors()
    {
        var json = $$"""
            {
              "Instances": [ {{ConfigTestHelper.Instance("main", 15)}} ],
              "Http": { "FeedTimeoutSeconds": 45, "DiscordTimeoutSeconds": 20, "ImageTimeoutSeconds": 10, "BlockPrivateNetworkRedirects": true },
              "Health": { "Enabled": false, "Port": 9090 },
              "Logging": { "Level": "warning" }
            }
            """;

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.True(ok, string.Join("\n", messages));
        Assert.Empty(messages);
    }

    [Fact]
    public void ConfigValidation_DuplicateInstanceIds_ReportsOneErrorNamingBoth()
    {
        var json = ConfigTestHelper.Config(
            ConfigTestHelper.Instance("main"),
            ConfigTestHelper.Instance("main"));

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.False(ok);
        Assert.Single(messages);
        Assert.Contains("Instances[0].Id", messages[0]);
        Assert.Contains("Instances[1].Id", messages[0]);
        Assert.Contains("'main'", messages[0]);
    }

    [Fact]
    public void ConfigValidation_IntervalZero_And_FifteenHundred_BothReportedInRange1To1440()
    {
        var json = ConfigTestHelper.Config(
            ConfigTestHelper.Instance("a", 0),
            ConfigTestHelper.Instance("b", 1500));

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.False(ok);
        Assert.Equal(2, messages.Count);   // both errors in one pass — proves non-fail-fast
        Assert.Contains(messages, m => m.StartsWith("Instances[0].CheckIntervalMinutes") && m.Contains("between 1 and 1440"));
        Assert.Contains(messages, m => m.StartsWith("Instances[1].CheckIntervalMinutes") && m.Contains("between 1 and 1440"));
    }

    [Fact]
    public void ConfigValidation_NonHttpFeedUrl_And_EmptyHost_BothReported()
    {
        var json = ConfigTestHelper.Config(ConfigTestHelper.Instance("a", 15, feedUrls: ["ftp://x", "https://"]));

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.False(ok);
        Assert.Equal(2, messages.Count);   // both feed URLs in one pass — proves non-fail-fast
        Assert.Contains(messages, m => m.StartsWith("Instances[0].Feeds[0].Url") && m.Contains("'ftp://x'"));
        Assert.Contains(messages, m => m.StartsWith("Instances[0].Feeds[1].Url") && m.Contains("'https://'"));
    }

    [Theory]
    [InlineData("http://discord.com/api/webhooks/123456789012345678/tok")]   // wrong scheme
    [InlineData("https://example.com/webhooks/123456789012345678/tok")]      // wrong host
    [InlineData("https://discord.com/api/webhooks/abc/tok")]                 // non-numeric id
    public void ConfigValidation_WebhookUrlWrongScheme_HostOrShape_ReportsPatternError(string webhookUrl)
    {
        var discord = $$"""{ "WebhookUrl": "{{webhookUrl}}" }""";
        var json = ConfigTestHelper.Config(ConfigTestHelper.Instance("a", 15, discord: discord));

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.False(ok);
        Assert.Single(messages);
        Assert.Contains("Instances[0].Discord.WebhookUrl", messages[0]);
        Assert.Contains("https://discord.com/api/webhooks/<id>/<token>", messages[0]);
    }

    [Fact]
    public void ConfigValidation_ColorOutOfRange_NegativeAndAboveMax_BothReported()
    {
        var low = $$"""{ "WebhookUrl": "{{ConfigTestHelper.WebhookUrl}}", "Color": -1 }""";
        var high = $$"""{ "WebhookUrl": "{{ConfigTestHelper.WebhookUrl}}", "Color": 16777216 }""";

        var json = ConfigTestHelper.Config(
            ConfigTestHelper.Instance("a", 15, discord: low),
            ConfigTestHelper.Instance("b", 15, discord: high));

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.False(ok);
        Assert.Equal(2, messages.Count);   // both colors in one pass — proves non-fail-fast
        Assert.Contains(messages, m => m.StartsWith("Instances[0].Discord.Color") && m.Contains("between 0 and 16777215"));
        Assert.Contains(messages, m => m.StartsWith("Instances[1].Discord.Color") && m.Contains("between 0 and 16777215"));
    }

    [Fact]
    public void ConfigValidation_ModeUnknownValue_MessageListsValidValues()
    {
        var discord = $$"""{ "WebhookUrl": "{{ConfigTestHelper.WebhookUrl}}", "Mode": "table" }""";
        var json = ConfigTestHelper.Config(ConfigTestHelper.Instance("a", 15, discord: discord));

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.False(ok);
        Assert.Single(messages);
        Assert.Contains("Instances[0].Discord.Mode", messages[0]);
        Assert.Contains("'embed'", messages[0]);
        Assert.Contains("'forum'", messages[0]);
        Assert.Contains("'markdown'", messages[0]);
    }

    [Fact]
    public void ConfigValidation_EmptyInstances_And_NoFeeds_BothReported()
    {
        var (okEmpty, messagesEmpty) = ConfigTestHelper.Run("""{ "Instances": [] }""");

        Assert.False(okEmpty);
        Assert.Single(messagesEmpty);
        Assert.Contains("Instances", messagesEmpty[0]);
        Assert.Contains("at least one instance", messagesEmpty[0]);

        var noFeeds = $$"""{ "Instances": [ { "Id": "a", "CheckIntervalMinutes": 15, "Feeds": [], "Discord": { "WebhookUrl": "{{ConfigTestHelper.WebhookUrl}}" } } ] }""";
        var (okNoFeeds, messagesNoFeeds) = ConfigTestHelper.Run(noFeeds);

        Assert.False(okNoFeeds);
        Assert.Single(messagesNoFeeds);
        Assert.Contains("Instances[0].Feeds", messagesNoFeeds[0]);
        Assert.Contains("at least one feed", messagesNoFeeds[0]);
    }

    [Fact]
    public void ConfigValidation_DuplicateFeedUrlWithinInstance_Reported()
    {
        var json = ConfigTestHelper.Config(ConfigTestHelper.Instance("a", 15, feedUrls: [ConfigTestHelper.FeedUrl, ConfigTestHelper.FeedUrl]));

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.False(ok);
        Assert.Single(messages);
        Assert.Contains("Instances[0].Feeds[0].Url", messages[0]);
        Assert.Contains("Instances[0].Feeds[1].Url", messages[0]);
        Assert.Contains(ConfigTestHelper.FeedUrl, messages[0]);
    }

    [Fact]
    public void ConfigValidation_MultipleDistinctErrors_AllCollectedInSinglePass()
    {
        var badDiscord = $$"""{ "WebhookUrl": "http://discord.com/api/webhooks/1/tok", "Mode": "nope" }""";
        var colorDiscord = $$"""{ "WebhookUrl": "{{ConfigTestHelper.WebhookUrl}}", "Color": -5 }""";

        var json = ConfigTestHelper.Config(
            ConfigTestHelper.Instance("a", 9999, discord: badDiscord, feedUrls: ["ftp://x"]),
            ConfigTestHelper.Instance("b", 15, discord: colorDiscord));

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.False(ok);
        // exactly the five violations — no more, no less
        Assert.Equal(5, messages.Count);
        Assert.Contains(messages, m => m.StartsWith("Instances[0].CheckIntervalMinutes"));
        Assert.Contains(messages, m => m.StartsWith("Instances[0].Feeds[0].Url"));
        Assert.Contains(messages, m => m.StartsWith("Instances[0].Discord.WebhookUrl"));
        Assert.Contains(messages, m => m.StartsWith("Instances[0].Discord.Mode"));
        Assert.Contains(messages, m => m.StartsWith("Instances[1].Discord.Color"));
    }

    [Fact]
    public void ConfigValidation_UnknownProperties_Tolerated()
    {
        var json = $$"""{ "Preview": 1, "Instances": [ { "Id": "a", "NewField": null, "CheckIntervalMinutes": 15, "Feeds": [ { "Url": "{{ConfigTestHelper.FeedUrl}}", "RetryCount": 3 } ], "Discord": { "WebhookUrl": "{{ConfigTestHelper.WebhookUrl}}", "MentionRoles": [] } } ] }""";

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.True(ok, string.Join("\n", messages));
        Assert.Empty(messages);
    }

    [Fact]
    public void ConfigValidation_MissingCheckIntervalMinutes_ReportedAsRequired()
    {
        var json = $$"""{ "Instances": [ { "Id": "a", "Feeds": [ { "Url": "{{ConfigTestHelper.FeedUrl}}" } ], "Discord": { "WebhookUrl": "{{ConfigTestHelper.WebhookUrl}}" } } ] }""";

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.False(ok);
        Assert.Single(messages);
        Assert.Contains("Instances[0].CheckIntervalMinutes", messages[0]);
        Assert.Contains("required", messages[0]);
    }

    [Fact]
    public void ConfigValidation_MissingDiscordSection_ReportedAsRequired()
    {
        var json = $$"""{ "Instances": [ { "Id": "a", "CheckIntervalMinutes": 15, "Feeds": [ { "Url": "{{ConfigTestHelper.FeedUrl}}" } ] } ] }""";

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.False(ok);
        Assert.Single(messages);
        Assert.Contains("Instances[0].Discord", messages[0]);
        Assert.Contains("required", messages[0]);
    }

    [Fact]
    public void ConfigValidation_HealthPortOutOfRange_ReportedInRange1To65535()
    {
        var json = $$"""{ "Instances": [ {{ConfigTestHelper.Instance("a")}} ], "Health": { "Port": 70000 } }""";

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.False(ok);
        Assert.Single(messages);
        Assert.Contains("Health.Port", messages[0]);
        Assert.Contains("between 1 and 65535", messages[0]);
    }

    [Fact]
    public void ConfigValidation_UnknownLogLevel_MessageListsValidValues()
    {
        var json = $$"""{ "Instances": [ {{ConfigTestHelper.Instance("a")}} ], "Logging": { "Level": "Verbose" } }""";

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.False(ok);
        Assert.Single(messages);
        Assert.Contains("Logging.Level", messages[0]);
        Assert.Contains("'Trace'", messages[0]);
        Assert.Contains("'Information'", messages[0]);
        Assert.Contains("'Critical'", messages[0]);
    }

    [Fact]
    public void ConfigValidation_MissingFeedUrl_ReportedAsRequired()
    {
        var json = ConfigTestHelper.Config(ConfigTestHelper.Instance("a", 15, feedUrls: [""]));

        var (ok, messages) = ConfigTestHelper.Run(json);

        Assert.False(ok);
        Assert.Single(messages);
        Assert.Contains("Instances[0].Feeds[0].Url", messages[0]);
        Assert.Contains("required", messages[0]);
    }
}
