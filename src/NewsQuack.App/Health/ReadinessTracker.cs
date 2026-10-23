namespace NewsQuack.App.Health;

/// <summary>
/// Tracks which components have reported ready so /readyz can answer with the exact missing set (T-030 — API frozen by the task).
/// Registered as a singleton: a component calls <see cref="RegisterRequirement"/> at wiring time and <see cref="MarkReady"/> when its
/// init completes. T-030 itself registers no requirements, so /readyz passes as soon as the listener is up; later tasks register and
/// mark their own components and never modify this file. Thread-safe: concurrent RegisterRequirement/MarkReady from multiple workers
/// are safe, and MarkReady is idempotent (repeating it changes nothing).
/// </summary>
public sealed class ReadinessTracker
{
    private readonly object _gate = new();

    // Registration order is preserved so the 503 "missing" array is deterministic; the set dedupes repeated registrations.
    private readonly List<string> _required = [];
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ready = new(StringComparer.Ordinal);

    /// <summary>Declares a component that must be ready before /readyz passes. Called at wiring time; re-registering the same name is a no-op.</summary>
    public void RegisterRequirement(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        lock (_gate)
            if (_registered.Add(name))   // Add returns false when the name was already registered — no duplicate in the missing set
                _required.Add(name);
    }

    /// <summary>Marks a component ready when its init completes. Idempotent; safe to call concurrently and for unregistered names.</summary>
    public void MarkReady(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        lock (_gate)
            _ready.Add(name);   // set semantics — calling again is a no-op
    }

    /// <summary>True iff every registered requirement is marked ready — vacuously true when nothing is registered.</summary>
    public bool IsReady
    {
        get { lock (_gate) return _required.All(_ready.Contains); }
    }

    /// <summary>Names not yet ready, in registration order (for the 503 body).</summary>
    public IReadOnlyCollection<string> Missing
    {
        get { lock (_gate) return _required.Where(name => !_ready.Contains(name)).ToList(); }
    }
}
