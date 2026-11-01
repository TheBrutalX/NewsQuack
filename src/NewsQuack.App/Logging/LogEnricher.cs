using Serilog.Context;

namespace NewsQuack.App.Logging;

/// <summary>
/// Per-worker enrichment context for structured logs (T-004; T-054: backed by Serilog's LogContext). Registered Scoped — one instance per worker scope.
/// A worker calls <see cref="Set"/> once when it starts its scope, then wraps its work in <see cref="Activate"/>:
/// while the returned scope is alive, every JSON log line emitted from that execution context carries
/// "instanceId" / "url" fields (only the values actually set); after dispose, lines omit them again.
/// The pushed properties flow through Serilog's LogContext — AsyncLocal-backed, so they flow correctly across awaits and are
/// concurrency-safe per execution context: two workers on different threads never see each other's enrichment. An <see cref="AsyncLocal{T}"/>
/// mirror keeps <see cref="Current"/> working for any consumer that wants the active enricher itself.
/// </summary>
public sealed class LogEnricher
{
    private static readonly AsyncLocal<LogEnricher?> Active = new();

    /// <summary>Name of the "instanceId" field in the frozen line format — shared with <see cref="SerilogJsonLineFormatter"/> (no duplicated literals).</summary>
    internal const string InstanceIdPropertyName = "instanceId";

    /// <summary>Name of the "url" field in the frozen line format — shared with <see cref="SerilogJsonLineFormatter"/> (no duplicated literals).</summary>
    internal const string UrlPropertyName = "url";

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
    /// Disposing restores whatever enrichment (if any) was active before — nested activations work.
    /// </summary>
    public static IDisposable Activate(LogEnricher enricher) => new ActiveScope(enricher);

    private sealed class ActiveScope : IDisposable
    {
        private readonly LogEnricher? _previous;
        private readonly IDisposable? _instanceIdScope;   // null when InstanceId was not set — nulls are never pushed (that is what preserves omit-when-absent)
        private readonly IDisposable? _urlScope;          // null when Url was not set

        public ActiveScope(LogEnricher enricher)
        {
            _previous = Active.Value;
            Active.Value = enricher;   // AsyncLocal mirror: keeps LogEnricher.Current working

            // Push only the values actually set — Serilog's LogContext carries them to every event logged from this execution context.
            // The trailing flag is Serilog's "destructure object" switch — false keeps the strings as scalars (they are always strings anyway).
            if (enricher.InstanceId is { } instanceId)
                _instanceIdScope = LogContext.PushProperty(InstanceIdPropertyName, instanceId, false);
            if (enricher.Url is { } url)
                _urlScope = LogContext.PushProperty(UrlPropertyName, url, false);
        }

        public void Dispose()
        {
            // Pop in reverse push order (LIFO) so a nested scope's values never leak past its outer one; Serilog's scopes are idempotent on re-dispose.
            _urlScope?.Dispose();
            _instanceIdScope?.Dispose();
            Active.Value = _previous;   // idempotent: the second dispose restores the same value again
        }
    }
}
