using Microsoft.Extensions.Logging;

namespace NewsQuack.App.Tests;

/// <summary>Thread-safe capture of structured log entries (level + rendered message + exception) for assertions. Fetcher tests log from many
/// threads at once (32 parallel fetches), hence the lock around the entry list.</summary>
internal sealed class LogCapture
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();

    public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    /// <summary>All captured entries in order (snapshot).</summary>
    public IReadOnlyList<Entry> Snapshot()
    {
        lock (_gate) return _entries.ToList();
    }

    /// <summary>The first entry at the given level whose message contains ALL of the fragments, or null when there is none.</summary>
    public Entry? Find(LogLevel level, params string[] fragments)
    {
        lock (_gate) return _entries.FirstOrDefault(e => e.Level == level && fragments.All(f => e.Message.Contains(f)));
    }

    /// <summary>How many entries were captured at the given level.</summary>
    public int CountAt(LogLevel level)
    {
        lock (_gate) return _entries.Count(e => e.Level == level);
    }

    /// <summary>A logger for the given service type, writing into this capture.</summary>
    public ILogger<TService> For<TService>() => new CapturedLogger<TService>(this);

    private sealed class CapturedLogger<TService> : ILogger<TService>
    {
        private readonly LogCapture _capture;

        public CapturedLogger(LogCapture capture) => _capture = capture;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_capture._gate)   // nested class — private access is legal in C#
                _capture._entries.Add(new Entry(logLevel, formatter(state, exception), exception));
        }
    }
}
