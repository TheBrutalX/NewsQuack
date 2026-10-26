using System.Text;
using System.Text.Json;
using NewsQuack.App.Logging;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;   // the real M.E.L. bridge — the same type Program.cs's ILoggerFactory produces at runtime

namespace NewsQuack.App.Tests;

/// <summary>
/// T-054 test capture: renders every captured LogEvent through the production <see cref="SerilogJsonLineFormatter"/> into a locked
/// StringWriter, so tests assert on the exact frozen line bytes (the lock mirrors the console sink's per-event gate — concurrent emits
/// can never interleave half-lines). M.E.L.-style loggers come from the real SerilogLoggerProvider bridge — the same type Program.cs's
/// ILoggerFactory produces at runtime. The capture configures nothing itself: callers pass the full LoggerConfiguration wiring (minimum
/// level, EF cap, FromLogContext) exactly as Program.cs does, and it only adds the collector sink.
/// </summary>
internal sealed class SerilogLineCapture : IDisposable
{
    private readonly object _gate = new();   // serializes line appends — not mutable state beyond the buffer, so concurrent loggers stay safe

    private readonly StringBuilder _output = new();
    private readonly SerilogJsonLineFormatter _formatter = new();   // stateless — one instance serves the whole capture
    private readonly SerilogLoggerProvider _provider;               // dispose: false (ctor default) — we own the Logger's lifetime below
    private readonly Logger _logger;

    public SerilogLineCapture(Action<LoggerConfiguration> configure)
    {
        var configuration = new LoggerConfiguration();
        configure(configuration);   // the caller's wiring — e.g. exactly Program.cs's minimum level + EF cap + FromLogContext
        configuration.WriteTo.Sink(new CollectorSink(this));   // collector sink: render through the production formatter, append under the gate

        _logger = configuration.CreateLogger();
        _provider = new SerilogLoggerProvider(_logger);
    }

    /// <summary>An M.E.L. logger for one category, obtained via the real bridge (the same type Program.cs's ILoggerFactory produces).</summary>
    public Microsoft.Extensions.Logging.ILogger Logger(string category) => _provider.CreateLogger(category);   // qualified: Serilog.ILogger shadows it in this file

    /// <summary>All captured lines (one JSON object per line, trailing newline removed).</summary>
    public string[] Lines()
    {
        lock (_gate)
            return _output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Parses one captured line as a self-contained JsonElement.</summary>
    public JsonElement ParseLine(int index = 0) => JsonSerializer.Deserialize<JsonElement>(Lines()[index]);

    internal void Append(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        _formatter.Format(logEvent, writer);   // the formatter writes its own trailing newline — the sink appends none

        lock (_gate)
            _output.Append(writer.ToString());
    }

    public void Dispose() => _logger.Dispose();   // the provider holds a reference with dispose: false — disposing the Logger is enough

    private sealed class CollectorSink : ILogEventSink
    {
        private readonly SerilogLineCapture _capture;

        public CollectorSink(SerilogLineCapture capture) => _capture = capture;

        public void Emit(LogEvent logEvent) => _capture.Append(logEvent);
    }
}
