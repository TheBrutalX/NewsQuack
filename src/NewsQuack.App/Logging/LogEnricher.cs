namespace NewsQuack.App.Logging;

/// <summary>
/// Per-worker enrichment context for structured logs (T-004). Registered Scoped — one instance per worker scope.
/// A worker calls <see cref="Set"/> once when it starts its scope, then wraps its work in <see cref="Activate"/>:
/// while the returned scope is alive, every JSON log line emitted from that execution context carries
/// "instanceId" / "url" fields (only the values actually set); after dispose, lines omit them again.
/// The active enricher lives in an <see cref="AsyncLocal{T}"/>, so it flows correctly across awaits and is
/// concurrency-safe per execution context — two workers on different threads never see each other's enrichment.
/// </summary>
public sealed class LogEnricher
{
    private static readonly AsyncLocal<LogEnricher?> Active = new();

    /// <summary>The enricher active in the current execution context, or null outside an activated worker scope.</summary>
    public static LogEnricher? Current => Active.Value;

    /// <summary>Instance id for enrichment ("instanceId" field), null until set.</summary>
    public string? InstanceId { get; private set; }

    /// <summary>URL for enrichment ("url" field), null until set.</summary>
    public string? Url { get; private set; }

    /// <summary>Sets both enrichment values at once — called once when the worker starts its scope. Nulls mean "not enriched with this value".</summary>
    public void Set(string? instanceId, string? url)
    {
        InstanceId = instanceId;
        Url = url;
    }

    /// <summary>
    /// Activates this enricher for the current execution context until the returned scope is disposed.
    /// Disposing restores whatever enricher (if any) was active before — nested activations work.
    /// </summary>
    public static IDisposable Activate(LogEnricher enricher) => new ActiveScope(enricher);

    private sealed class ActiveScope : IDisposable
    {
        private readonly LogEnricher? _previous;

        public ActiveScope(LogEnricher enricher)
        {
            _previous = Active.Value;
            Active.Value = enricher;
        }

        public void Dispose() => Active.Value = _previous;   // idempotent: the second dispose restores the same value again
    }
}
