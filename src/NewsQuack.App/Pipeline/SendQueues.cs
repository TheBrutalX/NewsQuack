using System.Diagnostics.CodeAnalysis;   // [MaybeNullWhen(false)] for the non-blocking TryRead out param
using System.Threading.Channels;   // Channel<T> — the bounded per-instance queue backing store
using NewsQuack.App.Configuration;   // NewsQuackConfig — the registry is built from the validated config at registration (decision 5)
using NewsQuack.Core;

namespace NewsQuack.App.Pipeline;

/// <summary>
/// T-024: minimal per-instance bounded send queue (coordinator decision 5). Backed by a bounded <see cref="Channel{T}"/> with
/// <c>FullMode = Wait</c> (a full queue DELAYS the enqueuer, it never drops) and <c>SingleReader = true</c> (T-021's single-reader worker owns
/// reading). Capacity is a fixed constant of 100 per instance — the schema is frozen, so no config key is invented; the constructor takes the
/// capacity with that default so tests can use a small one. The <see cref="ISendQueue"/> contract exposes only <see cref="EnqueueAsync"/>;
/// <see cref="ReadAsync"/> is internal, for the worker that this queue exists to feed (the interface doc comment anticipates exactly this).
/// </summary>
public sealed class BoundedSendQueue : ISendQueue
{
    /// <summary>The fixed v1.0 per-instance capacity — one constant, no config key (decision 5).</summary>
    public const int DefaultCapacity = 100;

    private readonly Channel<Post> _channel;

    public BoundedSendQueue(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The send queue needs a positive capacity.");

        _channel = Channel.CreateBounded<Post>(new BoundedChannelOptions(capacity)   // <Post> explicit: T cannot be inferred from the options argument
        {
            FullMode = BoundedChannelFullMode.Wait,   // full ⇒ the writer waits for space; items are never dropped (at-least-once upstream)
            SingleReader = true,                       // T-021's single-reader worker is the only reader
        });
    }

    /// <summary>Enqueues one post. On a full queue this WAITS (FullMode.Wait) until space is available — it never drops.</summary>
    public Task EnqueueAsync(Post post, CancellationToken ct = default) => _channel.Writer.WriteAsync(post, ct).AsTask();   // ValueTask → Task (the contract returns Task)

    /// <summary>Internal reader for T-021's single-reader worker — deliberately NOT part of the <see cref="ISendQueue"/> contract (decision 5).</summary>
    internal ValueTask<Post> ReadAsync(CancellationToken ct = default) => _channel.Reader.ReadAsync(ct);

    /// <summary>Internal non-blocking read (tests + a worker's drain): true and a post when one is available right now, else false — never waits.</summary>
    internal bool TryRead([MaybeNullWhen(false)] out Post post) => _channel.Reader.TryRead(out post);

    /// <summary>Internal pending-post count (T-031's drain start log + tests): <see cref="Channel{T}.Reader.Count"/> — cheaply known, no extra state.</summary>
    internal int PendingCount => _channel.Reader.Count;
}

/// <summary>
/// T-024: one bounded send queue per configured instance (coordinator decision 5). Built from the validated config at registration (singleton):
/// every instance id gets its own <see cref="BoundedSendQueue"/> with the default capacity. <see cref="For"/> returns that instance's queue or
/// throws <see cref="KeyNotFoundException"/> naming the id — an unknown instance is a programming error, surfaced loudly, never a silent no-op.
/// </summary>
public sealed class SendQueueRegistry
{
    private readonly IReadOnlyDictionary<string, ISendQueue> _byInstanceId;   // Ordinal — instance ids are case-sensitive (T-002 uniqueness is Ordinal)

    public SendQueueRegistry(NewsQuackConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var queues = new Dictionary<string, ISendQueue>(StringComparer.Ordinal);

        foreach (var instance in config.Instances ?? [])
        {
            // ! safe: boot validation guarantees every Id is non-null and unique — this guard only prevents a null dictionary key, it never skips a real instance.
            if (instance.Id is null) continue;

            queues[instance.Id] = new BoundedSendQueue();   // default capacity 100 per instance — no config key invented
        }

        _byInstanceId = queues;
    }

    /// <summary>This instance's send queue, or <see cref="KeyNotFoundException"/> naming the id when it was not configured.</summary>
    public ISendQueue For(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);

        return _byInstanceId.TryGetValue(instanceId, out var queue)
            ? queue
            : throw new KeyNotFoundException($"No send queue is registered for instance '{instanceId}'.");
    }
}
