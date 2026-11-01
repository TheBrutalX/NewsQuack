using System.ComponentModel.DataAnnotations;

namespace NewsQuack.App.Configuration;

/// <summary>One feed polled by an instance.</summary>
public sealed class FeedConfig
{
    /// <summary>Required http(s) URL with a host; unique within the instance (checked by <see cref="ConfigValidator"/>).</summary>
    [Required]
    [HttpUrl]
    public string? Url { get; init; }
}
