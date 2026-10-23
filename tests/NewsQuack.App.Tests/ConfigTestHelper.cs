using NewsQuack.App.Configuration;

namespace NewsQuack.App.Tests;

/// <summary>Materializes a config JSON string and runs it through parse + validator, returning (exitOk, messages).</summary>
internal static class ConfigTestHelper
{
    /// <summary>A Discord webhook URL that satisfies the frozen pattern.</summary>
    public const string WebhookUrl = "https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWx";

    /// <summary>A plain feed URL that satisfies the http(s)-with-host rule.</summary>
    public const string FeedUrl = "https://example.com/feed.xml";

    /// <summary>Materializes the JSON to a temp file and runs ConfigLoader.LoadAndValidate on it (the full boot path minus stderr/exit).</summary>
    public static ConfigLoadResult Load(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"newsquack-t002-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "newsquack.json");

        try
        {
            File.WriteAllText(file, json);
            return ConfigLoader.LoadAndValidate(file);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Materializes the JSON to a temp file and runs loader + validator; (exitOk, messages) is what boot would act on.</summary>
    public static (bool ExitOk, IReadOnlyList<string> Messages) Run(string json)
    {
        var result = Load(json);
        return (result.IsSuccess, result.Errors);
    }

    /// <summary>One instance; feed URLs default to the standard test URL. Pass "" for a feed entry without "Url".</summary>
    public static string Instance(string id, int intervalMinutes = 15, string? discord = null, params string[] feedUrls)
    {
        var feeds = (feedUrls.Length == 0 ? new[] { FeedUrl } : feedUrls)
            .Select(u => u is null || u.Length == 0 ? "{}" : $$"""{ "Url": "{{u}}" }""")
            .Aggregate("", (acc, f) => acc + (acc.Length == 0 ? "" : ", ") + f);

        var discordJson = discord ?? $$"""{ "WebhookUrl": "{{WebhookUrl}}" }""";

        return $$"""{ "Id": "{{id}}", "CheckIntervalMinutes": {{intervalMinutes}}, "Feeds": [ {{feeds}} ], "Discord": {{discordJson}} }""";
    }

    /// <summary>Wraps instance objects in a root config document.</summary>
    public static string Config(params string[] instances) => $$"""{ "Instances": [ {{string.Join(", ", instances)}} ] }""";
}
