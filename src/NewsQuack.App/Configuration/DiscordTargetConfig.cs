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

    /// <summary>T-048: minimum interval in seconds between two requests to this webhook URL (1..60). Omitted ⇒ default 2 s (≤ 30 req/min — Discord's
    /// per-webhook limit). Shared across instances configured with the same webhook URL; the strictest (smallest) interval wins on conflict.</summary>
    [Range(1, 60)]
    public int? MinSendIntervalSeconds { get; init; }
}
