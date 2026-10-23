namespace NewsQuack.App.Configuration;

/// <summary>Global logging settings. All properties optional (defaults shown).</summary>
public sealed class LoggingSectionConfig
{
    /// <summary>"Trace" | "Debug" | "Information" | "Warning" | "Error" | "Critical", case-insensitive. Default "Information".</summary>
    [EnumLike("Trace", "Debug", "Information", "Warning", "Error", "Critical")]
    public string? Level { get; init; } = "Information";
}
