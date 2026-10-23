using System.Text;
using Microsoft.Extensions.Logging;

namespace NewsQuack.App.Logging;

/// <summary>
/// ILoggerProvider that writes every entry as one compact JSON line on a TextWriter (stdout in the host) — the frozen
/// format is defined by <see cref="JsonLogFormatter"/>. Thread-safe for concurrent use: all writes are serialized through
/// one gate, so multiple workers logging at the same time can never interleave half-lines. The provider does no level
/// filtering itself — that stays with the host's standard logging configuration (SetMinimumLevel from Logging.Level).
/// </summary>
public sealed class JsonLoggerProvider : ILoggerProvider
{
    private readonly TextWriter _output;

    // Serializes line writes only — not mutable state, so concurrent loggers stay safe.
    private readonly object _gate = new();

    /// <param name="output">Stream to write lines to (Console.Out in the host). Not owned: this provider flushes but never disposes it.</param>
    public JsonLoggerProvider(TextWriter output) => _output = output ?? throw new ArgumentNullException(nameof(output));

    public ILogger CreateLogger(string categoryName) => new JsonLogger(categoryName, this);

    internal void WriteLine(string line)
    {
        lock (_gate)
            _output.WriteLine(line);   // one object per line; the gate keeps concurrent writes atomic on any TextWriter
    }

    /// <summary>
    /// Parses the schema's Logging.Level value into a LogLevel. Case-insensitive, matching T-002's validation (EnumLike).
    /// Null or empty means "not set" — the schema default Information applies. Validation guarantees only the six schema values
    /// reach this method from config; anything else is a programming error and throws.
    /// </summary>
    public static LogLevel ParseLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
            return LogLevel.Information;   // schema default when the section or value is omitted

        return level.Trim().ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "information" => LogLevel.Information,
            "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "critical" => LogLevel.Critical,
            _ => throw new ArgumentException(
                $"Unknown log level '{level}' — expected Trace, Debug, Information, Warning, Error or Critical (case-insensitive).",
                nameof(level))
        };
    }

    public void Dispose()
    {
        lock (_gate)
            _output.Flush();   // flush only — the output stream belongs to the caller (Console.Out must outlive the provider)
    }
}

/// <summary>Per-category logger bound to one <see cref="JsonLoggerProvider"/> sink.</summary>
internal sealed class JsonLogger : ILogger
{
    private readonly string _categoryName;
    private readonly JsonLoggerProvider _provider;

    public JsonLogger(string categoryName, JsonLoggerProvider provider)
    {
        _categoryName = categoryName ?? throw new ArgumentNullException(nameof(categoryName));
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;   // scopes are not part of the frozen line format

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Trace && logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        // The .NET 10 logging pipeline hands providers the structured state directly (no public entry timestamp), so the
        // UTC emission time is taken here — DateTime.UtcNow is exactly what the pipeline stamps internally.
        var message = formatter(state, exception);   // rendered template — placeholders already substituted
        _provider.WriteLine(JsonLogFormatter.Format(DateTime.UtcNow, logLevel, _categoryName, message, state, exception));
    }
}
