using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NewsQuack.App.Fetching;

/// <summary>
/// T-010: the validated fallback User-Agent cascade. Every candidate is validated at construction (non-empty after trim, ≤ 255 chars, no
/// control characters); invalid candidates are dropped with a Warning log and never sent (coordinator decision). The order of the surviving
/// candidates IS the retry order — on 401/403 the fetcher moves to the next one.
/// </summary>
public sealed class UserAgentResolver
{
    /// <summary>Candidates must be at most this long after trimming.</summary>
    public const int MaxCandidateLength = 255;

    /// <summary>The v1.0 cascade, in retry order (coordinator decision — both entries validate: non-empty, ≤ 255 chars, no control characters).</summary>
    public static readonly IReadOnlyList<string> DefaultCandidates = new[]
    {
        "NewsQuack/1.0 (+https://github.com/TheBrutalX/NewsQuack; +rss-reader)",
        "Mozilla/5.0 (compatible; NewsQuack/1.0; +https://github.com/TheBrutalX/NewsQuack)",
    };

    /// <summary>The surviving candidates, in retry order — empty when every candidate was invalid at construction.</summary>
    public IReadOnlyList<string> Candidates { get; }

    public UserAgentResolver(IEnumerable<string?>? candidates = null, ILogger<UserAgentResolver>? logger = null)   // string? elements: a null candidate is invalid and gets dropped with a Warning
    {
        var log = logger ?? NullLogger<UserAgentResolver>.Instance;
        var source = candidates ?? DefaultCandidates;
        var valid = new List<string>();

        foreach (var candidate in source)
        {
            if (IsValid(candidate))
                valid.Add(candidate!.Trim());   // ! safe: IsValid guarantees non-null here

            else
                log.LogWarning("Dropping invalid User-Agent candidate '{Candidate}' ({Length} chars) — must be non-empty after trim, ≤ {Max} chars, no control characters", Describe(candidate), candidate?.Length ?? 0, MaxCandidateLength);
        }

        Candidates = valid;
    }

    /// <summary>The construction-time validation: non-empty after trim, ≤ 255 chars, no control characters (checked on the trimmed value —
    /// that is what would be sent).</summary>
    internal static bool IsValid(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var trimmed = candidate.Trim();

        if (trimmed.Length > MaxCandidateLength)
            return false;

        foreach (var character in trimmed)   // no LINQ on spans here — a plain loop keeps the check dependency-free
            if (char.IsControl(character))
                return false;

        return true;
    }

    private static string Describe(string? candidate) => candidate is null ? "(null)" : $"'{candidate}'";
}
