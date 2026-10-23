using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NewsQuack.App.Logging;

/// <summary>
/// Renders one log entry as a single compact JSON line. The field names and shapes are FROZEN by T-004 — later tasks
/// and ops tooling bind to them:
/// {"@timestamp":"2026-10-23T14:05:09.123Z","level":"Information","logger":"...","message":"...","props":{...},"exception":null}
/// Enrichment fields "instanceId" / "url" are inserted after "logger", only when the current <see cref="LogEnricher"/>
/// has them set (never emitted as null). All strings go through <see cref="Utf8JsonWriter"/>, so quotes and newlines
/// (e.g. inside stack traces) are always properly escaped — a log entry never spans more than one line.
/// </summary>
public static class JsonLogFormatter
{
    /// <summary>ISO 8601 with millisecond precision and literal Z suffix — explicit pattern + invariant culture, never CurrentCulture.</summary>
    public const string TimestampPattern = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>
    /// Framework-internal key that Microsoft.Extensions.Logging's FormattedLogValues adds to every entry (a copy of the raw
    /// template). It is not caller-supplied state, so it never appears in "props" — with it stripped, an entry logged without
    /// structured properties has no props left and the field is omitted entirely, as the frozen format requires.
    /// </summary>
    private const string OriginalFormatKey = "{OriginalFormat}";

    /// <summary>
    /// Renders one JSON line for the given entry. <paramref name="timestamp"/> is converted to UTC first (a no-op when it is
    /// already Utc, which is what the logging pipeline hands us); field presence follows the frozen rules (props omitted when empty,
    /// exception null when absent, enrichment fields only when set).
    /// </summary>
    public static string Format(DateTime timestamp, LogLevel level, string loggerName, string message, object? state, Exception? exception)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            // Never local time: ToUniversalTime + explicit pattern + invariant culture kill the classic host-TZ bug.
            writer.WriteString("@timestamp", timestamp.ToUniversalTime().ToString(TimestampPattern, CultureInfo.InvariantCulture));
            writer.WriteString("level", level.ToString());
            writer.WriteString("logger", loggerName);

            var enricher = LogEnricher.Current;   // null outside an activated worker scope — both fields omitted then
            if (enricher?.InstanceId is { } instanceId)
                writer.WriteString("instanceId", instanceId);
            if (enricher?.Url is { } url)
                writer.WriteString("url", url);

            writer.WriteString("message", message);   // the rendered template — placeholders already substituted by the caller

            WriteProps(writer, state);

            if (exception is null)
            {
                writer.WriteNull("exception");
            }
            else
            {
                writer.WriteStartObject("exception");
                // FullName can be null for dynamic/compiled types — Name always exists and still identifies the type.
                writer.WriteString("type", exception.GetType().FullName ?? exception.GetType().Name);
                writer.WriteString("message", exception.Message);

                var stackTrace = exception.StackTrace;   // the full trace, untruncated; newlines escaped by the writer
                if (stackTrace is null)
                    writer.WriteNull("stackTrace");      // constructed but never thrown — no trace exists to serialize
                else
                    writer.WriteString("stackTrace", stackTrace);

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Writes "props" from the structured state; the field is omitted entirely when the state carries no properties.</summary>
    private static void WriteProps(Utf8JsonWriter writer, object? state)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> properties || properties.Count == 0)
            return;

        var hasProps = properties.Any(p => p.Key != OriginalFormatKey);   // framework-internal template copy is not caller state
        if (!hasProps)
            return;

        writer.WriteStartObject("props");
        foreach (var (key, value) in properties)
        {
            if (key == OriginalFormatKey)
                continue;
            WritePropertyValue(writer, key, value);
        }
        writer.WriteEndObject();
    }

    /// <summary>Serializes one property value with its default JSON representation — strings stay strings, everything else via System.Text.Json.</summary>
    private static void WritePropertyValue(Utf8JsonWriter writer, string key, object? value)
    {
        if (value is null)
        {
            writer.WriteNull(key);
            return;
        }

        if (value is string s)   // explicit: strings stay strings (the default representation would do the same — this documents the rule)
        {
            writer.WriteString(key, s);
            return;
        }

        writer.WritePropertyName(key);   // Serialize below writes only the value — the property name must be written first
        JsonSerializer.Serialize(writer, value, value.GetType());   // numbers/bools native, objects/arrays/enums via their default System.Text.Json representation
    }
}
