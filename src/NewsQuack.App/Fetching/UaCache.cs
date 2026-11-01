namespace NewsQuack.App.Fetching;

/// <summary>
/// T-010: bounded LRU cache of winning User-Agent candidates per feed URL. Thread-safe (concurrent feed fetches) — one lock around the
/// ordered structure, which is fine at this scale (coordinator decision). Both reads and writes refresh recency; when the cap is exceeded
/// the least-recently-used entry is evicted.
/// </summary>
public sealed class UaCache
{
    /// <summary>Default cap — 1000 URLs (constructor-injectable for tests).</summary>
    public const int DefaultCapacity = 1000;

    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Url, string UserAgent)>> _entries = new();
    private readonly LinkedList<(string Url, string UserAgent)> _recency = new();   // head = least recently used, tail = most recent

    public UaCache(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The UA cache needs a positive capacity.");

        _capacity = capacity;
    }

    /// <summary>Current entry count (tests/observability).</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Returns the cached candidate for <paramref name="url"/> and refreshes its recency. On a miss, <paramref name="userAgent"/>
    /// is empty (the value type is non-nullable — an absent entry has no value).</summary>
    public bool TryGet(string url, out string userAgent)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(url, out var node))
            {
                _recency.Remove(node);     // a read makes the entry most-recently-used — that is what LRU means here
                _recency.AddLast(node);
                userAgent = node.Value.UserAgent;
                return true;
            }

            userAgent = string.Empty;
            return false;
        }
    }

    /// <summary>Stores (or updates) the candidate for <paramref name="url"/> as most-recently-used, evicting least-recently-used entries
    /// until the cap holds.</summary>
    public void Set(string url, string userAgent)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(url, out var existing))
            {
                _recency.Remove(existing);
                existing.Value = (url, userAgent);   // refresh in place — same recency position moved to the tail below
                _recency.AddLast(existing);
                return;
            }

            var node = _recency.AddLast((url, userAgent));
            _entries[url] = node;

            while (_entries.Count > _capacity)   // evict least recently used until within the cap
            {
                var oldest = _recency.First!;    // ! safe: Count > capacity ≥ 1 implies the list is non-empty
                _recency.RemoveFirst();
                _entries.Remove(oldest.Value.Url);
            }
        }
    }
}
