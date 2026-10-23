using System.ComponentModel.DataAnnotations;

namespace NewsQuack.App.Configuration;

/// <summary>Discord target settings for one instance (the only notification target in v1.0).</summary>
public sealed class DiscordTargetConfig
{
    /// <summary>Required Discord webhook URL, pattern-validated.</summary>
    [Required]
    [DiscordWebhookUrl]
    public string? WebhookUrl { get; init; }

    /// <summary>"embed" | "forum" | "markdown", case-insensitive. Defaults to "embed" when omitted.</summary>
    [EnumLike("embed", "forum", "markdown")]
    public string? Mode { get; init; } = "embed";

    /// <summary>Optional Discord embed color, 0..16777215 (0xRRGGBB).</summary>
    [Range(0, 16777215)]
    public int? Color { get; init; }

    /// <summary>Optional http(s) URL with a host, used when an item has no image of its own.</summary>
    [HttpUrl]
    public string? FallbackImageUrl { get; init; }
}
