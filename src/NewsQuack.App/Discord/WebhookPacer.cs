namespace NewsQuack.App.Discord;

/// <summary>
/// T-048: client-side pacing for ONE webhook URL — no two requests to this URL may start closer together than the configured minimum interval.
/// Pacing semantics are pinned (task constraint 1): measure between request STARTS — before sending request N, wait until lastRequestStart + interval
/// has elapsed; the first request to a URL is never delayed. A single-slot gate serializes start-slot reservation so concurrent callers (instances sharing
/// this URL) never reserve the same slot: each waiter holds the gate through its floor wait and stamps <c>_lastStartUtc</c> at reservation, which makes
/// every successive start ≥ interval after the previous one even under concurrency. The pacing wait honors cancellation end-to-end (constraint 6): an OCE
/// surfaces promptly from either the gate or the floor delay, no slot is reserved by a cancelled caller, and nothing blocks — a shutdown drain never waits
/// out a pace floor. Pacing COMPOSES with, never replaces, the worker's retry/backoff (constraint 5): this class only delays request starts; a long
/// Retry-After still dominates because by the time it expires <c>_lastStartUtc</c> is stale by far more than one interval.
/// </summary>
public sealed class WebhookPacer
{
    private readonly TimeSpan _interval;
    private readonly object _stampLock = new();   // guards _startStampsUtc (appended at reservation, snapshotted by tests)
    private readonly List<DateTime> _startStampsUtc = new();   // every reserved start, in order — the ground truth for "measure between request STARTS"
    private readonly SemaphoreSlim _gate = new(1, 1);   // serializes start-slot reservation — released in finally on every path (success / OCE), T-010 balance pattern
    private DateTime? _lastStartUtc;                     // the last reserved request start — null until the first request, which is never delayed
    private int _activeWaits;                            // internal test hook: callers currently inside WaitUntilAllowedAsync

    public WebhookPacer(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The pacing interval must be positive.");

        _interval = interval;
    }

    /// <summary>The configured minimum interval between request starts for this URL.</summary>
    public TimeSpan Interval => _interval;

    /// <summary>Internal (test hook): callers currently blocked in <see cref="WaitUntilAllowedAsync"/> — 0 when nobody is pacing. Lets tests wait for a
    /// specific post to be inside the pacing wait before cancelling, without timing races.</summary>
    internal int ActiveWaits => Volatile.Read(ref _activeWaits);

    /// <summary>Internal (test hook): every reserved request start for this URL, in order (a snapshot copy). These are the ground truth for "no two requests
    /// start closer than the interval" assertions — unlike server-side arrival stamps, which can lag a request's actual start by variable server processing time.</summary>
    internal List<DateTime> StartStampsUtc
    {
        get
        {
            lock (_stampLock)
                return new List<DateTime>(_startStampsUtc);   // snapshot — the list keeps growing on live pacers
        }
    }

    /// <summary>Waits until this request may start: a no-op when the floor has already elapsed (including the first request — it is never delayed),
    /// otherwise a token-supervised delay until lastStart + interval. Reserves this request's start slot on return; the caller then sends immediately.</summary>
    public async Task WaitUntilAllowedAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _activeWaits);

        try
        {
            await _gate.WaitAsync(ct);   // OCE on cancellation — prompt, no slot reserved

            try
            {
                if (_lastStartUtc is { } lastStart)
                {
                    var floor = lastStart + _interval;
                    var now = DateTime.UtcNow;   // UTC only (AGENTS.md) — duration math, culture-agnostic

                    if (now < floor)
                        await Task.Delay(floor - now, ct);   // token-supervised: cancellation during the pace wait surfaces promptly (constraint 6)
                }

                _lastStartUtc = DateTime.UtcNow;   // reserve this request's start slot — we hold the gate, so no other caller can stamp before us
                lock (_stampLock)
                    _startStampsUtc.Add(_lastStartUtc.Value);   // record the reserved start (test hook) — same instant as the reservation above
            }
            finally
            {
                _gate.Release();   // every path: success / OCE from the gate or the delay (balance property)
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeWaits);
        }
    }
}
