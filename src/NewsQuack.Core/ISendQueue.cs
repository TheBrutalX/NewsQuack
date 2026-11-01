namespace NewsQuack.Core;

/// <summary>Per-instance bounded queue; deliberately minimal: the concrete implementation also exposes a reader used by its own single-reader worker.</summary>
public interface ISendQueue
{
    Task EnqueueAsync(Post post, CancellationToken ct = default);
}
