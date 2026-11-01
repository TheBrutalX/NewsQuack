namespace NewsQuack.Core;

/// <summary>The single mutable object shared by all pipeline stages for one item.</summary>
public sealed class PostContext
{
    public required string InstanceId { get; init; }
    public required Post CurrentPost { get; set; }              // stages replace it via 'with' (e.g. resolved image) — never mutate in place
    public bool Skip { get; set; }                              // any stage may set true to stop processing of this item
}
