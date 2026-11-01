namespace NewsQuack.App.Fetching;

/// <summary>The outcome of parsing one robots.txt for one path: whether the path is allowed, and how many malformed lines were skipped.</summary>
public sealed record RobotsParseResult(bool Allowed, int MalformedLineCount);

/// <summary>
/// T-010: dedicated, pure, tolerant robots.txt parser (coordinator decision). Line-based; it NEVER throws on malformed input — a line without
/// ':' is skipped and counted, not an exception. Matching (simplified RFC 9309 spirit): blocks whose <c>User-agent:</c> value matches
/// <c>*</c> or <c>newsquack</c> (case-insensitive) contribute their rules; among the applicable rules the longest matching path prefix wins,
/// Allow beats Disallow on an equal-length tie, and a bare <c>Disallow:</c> with an empty value allows everything.
/// </summary>
public static class RobotsParser
{
    /// <summary>The product token matched in addition to <c>*</c> (coordinator decision).</summary>
    public const string AgentName = "newsquack";

    public static RobotsParseResult Parse(string? robotsText, string path)
    {
        if (string.IsNullOrEmpty(robotsText))
            return new RobotsParseResult(true, 0);   // no rules at all → everything allowed

        var blocks = new List<Block>();
        var malformed = 0;

        foreach (var rawLine in robotsText.Split('\n'))
        {
            var line = rawLine.Trim();               // trims both ends — a stray '\r' from CRLF files is whitespace too

            if (line.Length == 0) continue;          // blank line — skipped, not counted
            if (line[0] == '#') continue;            // comment — skipped, not counted

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                malformed++;                         // malformed — skipped and counted, never a FormatException
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();   // values AND keys are trimmed of surrounding whitespace
            var value = line[(colon + 1)..].Trim();

            if (key == "user-agent")
            {
                // Consecutive User-agent lines describe one group of agents sharing the rules that follow them.
                if (blocks.Count > 0 && blocks[^1].Rules.Count == 0)
                    blocks[^1].Agents.Add(value);
                else
                    blocks.Add(new Block([value]));
            }
            else if (key is "allow" or "disallow")
            {
                if (blocks.Count == 0) continue;     // a rule before any User-agent line applies to no block — skipped, tolerant
                blocks[^1].Rules.Add(new Rule(key == "allow", value));
            }
            // Any other directive (Sitemap:, Crawl-delay, …) is present-and-valid but not ours — ignored.
        }

        var matching = new List<Rule>();
        foreach (var block in blocks)
            if (block.Agents.Any(agent => agent == "*" || agent.Equals(AgentName, StringComparison.OrdinalIgnoreCase)))
                matching.AddRange(block.Rules);

        return new RobotsParseResult(Decide(matching, path), malformed);
    }

    /// <summary>Longest matching path prefix wins; on an equal-length tie Allow beats Disallow; a bare empty-value "Disallow:" allows
    /// everything (an empty value is a prefix of every path, so bestLength 0 means only such rules matched).</summary>
    private static bool Decide(List<Rule> rules, string path)
    {
        var bestLength = -1;
        var allowAtBest = false;

        foreach (var rule in rules)
        {
            if (!path.StartsWith(rule.Value, StringComparison.Ordinal)) continue;   // empty value → prefix of every path

            if (rule.Value.Length > bestLength)
            {
                bestLength = rule.Value.Length;
                allowAtBest = rule.Allow;
            }
            else if (rule.Value.Length == bestLength && rule.Allow)
                allowAtBest = true;   // tie: Allow beats Disallow
        }

        if (bestLength < 0) return true;    // no applicable rule matches the path → allowed
        if (bestLength == 0) return true;   // only bare empty-value rules matched — a bare "Disallow:" allows everything
        return allowAtBest;
    }

    private sealed class Block
    {
        public List<string> Agents { get; } = new();
        public List<Rule> Rules { get; } = new();

        public Block(IEnumerable<string> agents) => Agents.AddRange(agents);
    }

    private sealed record Rule(bool Allow, string Value);
}
