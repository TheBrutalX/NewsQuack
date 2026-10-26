using Serilog.Events;

namespace NewsQuack.App.Logging;

/// <summary>
/// Level mapping between the schema's frozen Logging.Level vocabulary (M.E.L. names — Trace/Debug/Information/Warning/Error/Critical)
/// and Serilog levels (T-054). Replaces T-004's provider-side level parsing with the exact same contract: null or empty means
/// "not set" — the schema default Information applies; matching is case-insensitive (T-002's EnumLike validation); anything else is
/// a programming error and throws. Validation guarantees only the six schema values reach this method from config.
/// </summary>
public static class SerilogLevels
{
    /// <summary>Parses the schema's Logging.Level value into a Serilog level — T-004's exact contract, mapped onto Serilog's vocabulary.</summary>
    public static LogEventLevel ParseLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
            return LogEventLevel.Information;   // schema default when the section or value is omitted

        return level.Trim().ToLowerInvariant() switch
        {
            "trace" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "information" => LogEventLevel.Information,
            "warning" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "critical" => LogEventLevel.Fatal,
            _ => throw new ArgumentException(
                $"Unknown log level '{level}' — expected Trace, Debug, Information, Warning, Error or Critical (case-insensitive).",
                nameof(level))
        };
    }

    /// <summary>
    /// Inverse name mapping for the frozen "level" field: Serilog's edge vocabulary must read as M.E.L. names in the JSON line —
    /// "Verbose" would render as "Trace", "Fatal" as "Critical"; the middle four names are identical in both vocabularies.
    /// </summary>
    internal static string ToFrozenLevelName(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "Trace",
        LogEventLevel.Fatal => "Critical",
        _ => level.ToString()   // Debug/Information/Warning/Error — same spelling in both vocabularies; None is unreachable (the bridge never emits it)
    };
}
