using System.ComponentModel.DataAnnotations;

namespace NewsQuack.App.Configuration;

/// <summary>Global health endpoint settings. All properties optional (defaults shown).</summary>
public sealed class HealthSectionConfig
{
    /// <summary>Whether the health endpoint is enabled. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>TCP port of the health endpoint, 1..65535. Default 8081.</summary>
    [Range(1, 65535)]
    public int Port { get; init; } = 8081;
}
