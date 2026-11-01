namespace NewsQuack.App.Fetching;

/// <summary>
/// T-010: the single global HTTP throttle shared by every request the feed fetcher makes (feed GETs AND robots.txt fetches). One
/// <see cref="SemaphoreSlim"/>, a fixed 8 slots for v1.0 — no config key invented (coordinator decision). Every wait must be paired with
/// exactly one release in a <c>finally</c> block on every path (success, HTTP error, transport error, cancellation) — the balance property
/// T-041 pins down later; it must already hold here.
/// </summary>
public sealed class GlobalHttpThrottle
{
    /// <summary>The v1.0 fixed slot count — one constant for the whole process.</summary>
    public const int DefaultMaxConcurrent = 8;

    private readonly SemaphoreSlim _semaphore;

    /// <summary>Slots still available right now (initially <see cref="DefaultMaxConcurrent"/>; back to it only when every wait has been released).</summary>
    public int AvailableCount => _semaphore.CurrentCount;

    public GlobalHttpThrottle(int maxConcurrent = DefaultMaxConcurrent)
    {
        if (maxConcurrent <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrent), maxConcurrent, "The throttle needs at least one slot.");

        _semaphore = new SemaphoreSlim(maxConcurrent);
    }

    /// <summary>Waits for a slot. Caller cancellation while waiting throws <see cref="OperationCanceledException"/> WITHOUT taking a slot —
    /// the caller then releases nothing (it owns no slot).</summary>
    public Task WaitAsync(CancellationToken ct) => _semaphore.WaitAsync(ct);

    /// <summary>Returns one previously taken slot. Must be called exactly once per successful <see cref="WaitAsync"/>.</summary>
    public void Release() => _semaphore.Release();
}
