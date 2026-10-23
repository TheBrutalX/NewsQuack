using System.ComponentModel.DataAnnotations;

namespace NewsQuack.App.Configuration;

/// <summary>One polling instance: its own feeds, check interval and Discord webhook.</summary>
public sealed class InstanceConfig
{
    /// <summary>Required, non-empty and unique across all instances (uniqueness is checked by <see cref="ConfigValidator"/>).</summary>
    [Required]
    public string? Id { get; init; }

    /// <summary>Required, 1..1440 minutes. Nullable so an omitted value is reported as "required" instead of silently defaulting.</summary>
    [Required]
    [Range(1, 1440)]
    public int? CheckIntervalMinutes { get; init; }

    /// <summary>Required, at least one feed with a URL unique within the instance (checked by <see cref="ConfigValidator"/>).</summary>
    public List<FeedConfig>? Feeds { get; init; } = [];

    /// <summary>Required in v1.0 — Discord is the only notification target (null check in <see cref="ConfigValidator"/>, not an attribute, so the message can name what to add).</summary>
    public DiscordTargetConfig? Discord { get; init; }
}
