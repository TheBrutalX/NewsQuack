namespace NewsQuack.Core;

/// <summary>IsolateItem = drop the item on failure (the norm); ContinueDegraded = log and continue with a degraded post (optional enrichment only).</summary>
public enum FailurePolicy { IsolateItem, ContinueDegraded }

public interface IPostStage
{
    int Order { get; }                                          // lower runs first
    string Name { get; }                                        // stable name for structured logging
    FailurePolicy FailurePolicy { get; }
    Task ExecuteAsync(PostContext ctx, CancellationToken ct = default);
}
