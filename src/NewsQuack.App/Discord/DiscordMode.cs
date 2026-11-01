namespace NewsQuack.App.Discord;

/// <summary>The three Discord webhook payload modes (the config "Mode" values, matched case-insensitively).</summary>
public enum DiscordMode
{
    Embed,
    Forum,
    Markdown,
}
