namespace NewsQuack.App.Configuration;

/// <summary>Outcome of loading + validating the config file. Success requires a model and zero errors.</summary>
public sealed record ConfigLoadResult(NewsQuackConfig? Config, IReadOnlyList<string> Errors)
{
    public bool IsSuccess => Config is not null && Errors.Count == 0;
}
