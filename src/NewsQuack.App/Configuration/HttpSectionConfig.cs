using System.ComponentModel.DataAnnotations;

namespace NewsQuack.App.Configuration;

/// <summary>Global HTTP settings. All properties optional (defaults shown).</summary>
public sealed class HttpSectionConfig
{
    /// <summary>Feed fetch timeout in seconds, &gt; 0. Default 30.</summary>
    [Range(1, int.MaxValue)]
    public int FeedTimeoutSeconds { get; init; } = 30;

    /// <summary>Discord send timeout in seconds, &gt; 0. Default 15.</summary>
    [Range(1, int.MaxValue)]
    public int DiscordTimeoutSeconds { get; init; } = 15;

    /// <summary>Image fetch timeout in seconds, &gt; 0. Default 15.</summary>
    [Range(1, int.MaxValue)]
    public int ImageTimeoutSeconds { get; init; } = 15;

    /// <summary>Anti-SSRF switch consumed by the HTTP layer later. Default true (OWASP A10, issue #76): redirect hops to loopback/link-local/private
    /// addresses are rejected unless explicitly disabled — feeds that legitimately redirect into private networks opt out with `false`.</summary>
    public bool BlockPrivateNetworkRedirects { get; init; } = true;
}
