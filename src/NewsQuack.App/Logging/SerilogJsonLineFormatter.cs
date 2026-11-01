using System.Globalization;
using System.Text;
using System.Text.Json;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Parsing;   // TextToken/PropertyToken — the M.E.L.-faithful message renderer walks the parsed template (see WriteMessage)

namespace NewsQuack.App.Logging;

/// <summary>
/// Renders one log event as a single compact JSON line. The field names and shapes are FROZEN by T-004 — later tasks
/// and ops tooling bind to them:
/// {"@timestamp":"2026-10-23T14:05:09.123Z","level":"Information","logger":"...","message":"...","props":{...},"exception":null}
/// T-054 re-points the frozen shape at Serilog events — an output template cannot express it (no JSON escaping, no open-set
/// "props" object, no {type,message,stackTrace} exception object), so this ITextFormatter is mandatory. Enrichment fields
/// "instanceId" / "url" are hoisted to top level after "logger", only when the current <see cref="LogEnricher"/> pushed them
/// (never emitted as null). All strings go through <see cref="Utf8JsonWriter"/>, so quotes and newlines (e.g. inside stack
/// traces) are always properly escaped — a log entry never spans more than one line. The formatter is stateless: thread
/// safety comes from the sink's per-event lock around Format+Flush (the console sink provides it).
/// </summary>
public sealed class SerilogJsonLineFormatter : ITextFormatter
{
    /// <summary>ISO 8601 with millisecond precision and literal Z suffix — explicit pattern + invariant culture, never CurrentCulture.</summary>
    public const string TimestampPattern = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>
    /// Framework-internal key that Microsoft.Extensions.Logging's FormattedLogValues adds to every entry (a copy of the raw
    /// template). It is not caller-supplied state, so it never appears in "props" — with it stripped, an entry logged without
    /// structured properties has no props left and the field is omitted entirely, as the frozen format requires. The v10 M.E.L.
    /// bridge consumes it before Serilog sees it; the skip stays as a one-line guard against provider drift.
    /// </summary>
    private const string OriginalFormatKey = "{OriginalFormat}";

    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            // Never local time: LogEvent.Timestamp is a local DateTimeOffset — ToUniversalTime + explicit pattern + invariant culture kill the classic host-TZ bug.
            writer.WriteString("@timestamp", logEvent.Timestamp.ToUniversalTime().ToString(TimestampPattern, CultureInfo.InvariantCulture));
            writer.WriteString("level", SerilogLevels.ToFrozenLevelName(logEvent.Level));   // M.E.L. vocabulary — never "Verbose"/"Fatal"

            // Serilog 4 carries the source context as a regular property (the M.E.L. bridge sets it to the category name).
            var logger = logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var sourceContext) &&
                         sourceContext is ScalarValue { Value: string context }
                ? context
                : string.Empty;
            writer.WriteString("logger", logger);

            // Enrichment is hoisted to top level (in this order) and never rendered into "props".
            if (TryGetNonNullScalar(logEvent.Properties, LogEnricher.InstanceIdPropertyName, out var instanceId))
                WritePropertyValue(writer, LogEnricher.InstanceIdPropertyName, instanceId);
            if (TryGetNonNullScalar(logEvent.Properties, LogEnricher.UrlPropertyName, out var url))
                WritePropertyValue(writer, LogEnricher.UrlPropertyName, url);

            WriteMessage(writer, logEvent);   // T-004's M.E.L.-rendered message — NOT Serilog's RenderMessage() (it would quote string values)

            WriteProps(writer, logEvent.Properties);

            if (logEvent.Exception is null)
            {
                writer.WriteNull("exception");
            }
            else
            {
                var exception = logEvent.Exception;   // Serilog 4 stores the raw exception — T-004's rendering rules apply directly

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

        output.Write(Encoding.UTF8.GetString(buffer.ToArray()));
        output.WriteLine();   // exactly one trailing newline — the console sink appends none of its own
    }

    /// <summary>
    /// Renders "message" with T-004's exact semantics — what M.E.L.'s FormattedLogValues.ToString() produced before the swap: strings as-is,
    /// null arguments as "(null)", everything else invariant-culture IFormattable. Serilog's RenderMessage() is NOT used because it quotes
    /// string scalars (e.g. <c>feed "https://…"</c>), which would break the frozen line byte-for-byte. JSON escaping of whatever comes out
    /// happens in the Utf8JsonWriter either way.
    /// </summary>
    private static void WriteMessage(Utf8JsonWriter writer, LogEvent logEvent)
    {
        // M.E.L.'s zero-argument path returns the raw format string untouched (doubled braces NOT unescaped) — MessageTemplate.Text is that raw input.
        if (!HasCallerProperties(logEvent.Properties))
        {
            writer.WriteString("message", logEvent.MessageTemplate.Text);
            return;
        }

        using var message = new StringWriter();   // M.E.L.'s with-arguments path is String.Format — braces unescaped, placeholders substituted
        foreach (var token in logEvent.MessageTemplate.Tokens)
        {
            if (token is PropertyToken property &&
                logEvent.Properties.TryGetValue(property.PropertyName, out var bound) &&
                bound is ScalarValue scalar)
            {
                WriteMelScalar(message, scalar.Value, property.Format);   // M.E.L. semantics for the bound value (strings as-is — Serilog would quote them)
                continue;
            }

            token.Render(logEvent.Properties, message);   // literal text (braces unescaped by the parser), an unbound placeholder (Serilog's own RawText fallback — M.E.L. always binds every placeholder of {OriginalFormat}), or a non-scalar binding (the original object's ToString() is unreachable here — Serilog's rendering is the closest faithful representation)
        }

        writer.WriteString("message", message.ToString());
    }

    /// <summary>True when the event carries at least one caller property — i.e. M.E.L. was called with arguments (enrichment and source context are not caller state).</summary>
    private static bool HasCallerProperties(IReadOnlyDictionary<string, LogEventPropertyValue> properties) =>
        properties.Keys.Any(key => !IsSkippedPropsKey(key));

    /// <summary>Renders one bound scalar with M.E.L.'s String.Format semantics — the exact rules T-004's "message" field was rendered with.</summary>
    private static void WriteMelScalar(StringWriter output, object? value, string? format)
    {
        if (value is null)
        {
            output.Write("(null)");   // .NET 10's FormattedLogValues renders a null argument as "(null)" — pinned by T-004's behavior
            return;
        }

        if (value is string s)
        {
            output.Write(s);   // as-is, never quoted — the frozen line's JSON escaping happens in the Utf8JsonWriter
            return;
        }

        if (value is IFormattable formattable)
            output.Write(formattable.ToString(format, CultureInfo.InvariantCulture));   // String.Format's exact semantics for non-string values (incl. format specifiers like "{CutoffUtc:o}")
        else
            output.Write(value.ToString());
    }

    /// <summary>Writes "props" from the caller's properties; the field is omitted entirely when nothing remains after stripping enrichment and Serilog-internal keys.</summary>
    private static void WriteProps(Utf8JsonWriter writer, IReadOnlyDictionary<string, LogEventPropertyValue> properties)
    {
        var hasProps = false;
        foreach (var key in properties.Keys)
            if (!IsSkippedPropsKey(key))   // enrichment fields are hoisted to top level; framework-internal keys are not caller state
                hasProps = true;

        if (!hasProps)
            return;

        writer.WriteStartObject("props");
        foreach (var (key, value) in properties)
        {
            if (IsSkippedPropsKey(key))
                continue;
            WritePropertyValue(writer, key, value);
        }
        writer.WriteEndObject();
    }

    /// <summary>Keys that never reach "props": the two enrichment fields (hoisted to top level), the framework-internal template copy, and Serilog's own source-context metadata.</summary>
    private static bool IsSkippedPropsKey(string key) =>
        key == LogEnricher.InstanceIdPropertyName ||
        key == LogEnricher.UrlPropertyName ||
        key == OriginalFormatKey ||
        key == Constants.SourceContextPropertyName;

    /// <summary>True when the property holds a non-null scalar — the shape <see cref="LogEnricher"/> pushes (a string, never null).</summary>
    private static bool TryGetNonNullScalar(IReadOnlyDictionary<string, LogEventPropertyValue> properties, string name, out LogEventPropertyValue value) =>
        properties.TryGetValue(name, out value!) && value is ScalarValue { Value: not null };

    /// <summary>Writes one property (name + value) with its default JSON representation — strings stay strings, everything else via System.Text.Json.</summary>
    private static void WritePropertyValue(Utf8JsonWriter writer, string key, LogEventPropertyValue value)
    {
        writer.WritePropertyName(key);   // WriteValue below writes only the value — the property name must be written first
        WriteValue(writer, value);
    }

    /// <summary>Writes one property VALUE (no name) with its default JSON representation.</summary>
    private static void WriteValue(Utf8JsonWriter writer, LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue scalar:   // the M.E.L. bridge hands caller values over as scalars (incl. reflection types) — unwrap and render natively
                WriteScalar(writer, scalar.Value);
                break;

            case StructureValue structure:   // destructured objects — a JSON object of their properties; Serilog's own "@"-prefixed markers never reach the line
                writer.WriteStartObject();
                foreach (var member in structure.Properties)   // IReadOnlyList<LogEventProperty> in Serilog 4
                {
                    if (member.Name.StartsWith("@", StringComparison.Ordinal))
                        continue;   // e.g. the truncation marker — Serilog-internal, not caller state
                    WritePropertyValue(writer, member.Name, member.Value);
                }
                writer.WriteEndObject();
                break;

            case SequenceValue sequence:   // arrays/enumerables — a JSON array of the same per-element rules
                writer.WriteStartArray();
                foreach (var element in sequence.Elements)   // IReadOnlyList<LogEventPropertyValue> in Serilog 4
                    WriteValue(writer, element);
                writer.WriteEndArray();
                break;

            default:   // defensive: any exotic value type renders as its string representation — one bad value can never break the frozen line format or 500 the request through Logger.ThrowLoggingError (the T-004 rule)
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    /// <summary>Serializes one scalar with its default JSON representation — strings stay strings, nulls become JSON null, everything else via System.Text.Json.</summary>
    private static void WriteScalar(Utf8JsonWriter writer, object? value)
    {
        if (value is null)
        {
            writer.WriteRawValue("null");   // a caller property explicitly set to null — rendered as JSON null, exactly like T-004's state values (no keyless WriteNull() overload exists)
            return;
        }

        if (value is string s)   // explicit: strings stay strings (the default representation would do the same — this documents the rule)
        {
            writer.WriteStringValue(s);
            return;
        }

        JsonElement element;   // SerializeToElement is all-or-nothing: on failure nothing has been written to the line yet
        try
        {
            element = JsonSerializer.SerializeToElement(value, value.GetType());   // numbers/bools native, objects/arrays/enums via their default System.Text.Json representation
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Some framework-internal state values are not JSON-serializable by design — e.g. a RuntimeMethodInfo in ASP.NET Core routing diagnostics. Fall back to the string representation so one bad value can never break the frozen line format; the fallback IS the observable representation (the T-004 rule).
            writer.WriteStringValue(value.ToString());
            return;
        }

        element.WriteTo(writer);   // writes only the value — the property name was already written by WritePropertyValue
    }
}
