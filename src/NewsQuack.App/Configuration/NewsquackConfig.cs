namespace NewsQuack.App.Configuration;

/// <summary>
/// Root of the v2 config schema (frozen by T-002 — later tasks bind to these exact key names).
/// Property names are the JSON keys. Unknown JSON properties are tolerated by the serializer (forward compatibility).
/// </summary>
public sealed class NewsquackConfig
{
    /// <summary>Required, at least one instance. Presence and count are checked by <see cref="ConfigValidator"/> (annotations cannot express "at least one").</summary>
    public List<InstanceConfig>? Instances { get; init; } = [];

    /// <summary>All properties optional; defaults apply when the section or a property is omitted.</summary>
    public HttpSectionConfig? Http { get; init; } = new();

    /// <summary>All properties optional; defaults apply when the section or a property is omitted.</summary>
    public HealthSectionConfig? Health { get; init; } = new();

    /// <summary>All properties optional; defaults apply when the section or a property is omitted.</summary>
    public LoggingSectionConfig? Logging { get; init; } = new();
}
