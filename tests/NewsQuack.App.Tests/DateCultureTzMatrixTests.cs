using System.Diagnostics;
using System.Text;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-042: culture/timezone hostility matrix for feed date parsing — same fixtures ⇒ byte-identical UTC dates under every cell.
/// Matrix: TZ ∈ {UTC, America/Los_Angeles} × culture ∈ {invariant, de-DE, it-IT}, one explicit [Fact] per cell (coordinator
/// decision 4). Each cell runs in a separate child process (coordinator decision 1): mutating the TZ environment variable at
/// runtime does not change TimeZoneInfo.Local on net10/Linux (it is cached per process), so an in-process TZ cell would pass
/// vacuously. The child inherits the parent's environment with only TZ changed, for its whole lifetime; its culture is set
/// from its argument at startup. Each cell asserts against the single shared reference table (coordinator decision 5)
/// byte-for-byte — no fuzzy "dates are close" assertions. A failing cell is a real defect in the T-012 parser, never a reason
/// to weaken an assertion.
/// </summary>
public class DateCultureTzMatrixTests
{
    private static readonly TimeSpan CellTimeout = TimeSpan.FromSeconds(120);

    // ── Six explicit cells (one per matrix cell) ───────────────────────────────────────────────────────────────

    [Fact] public async Task TzUtc_CultureInvariant_MatchesReferenceTable() => await AssertCellMatchesReferenceTable("UTC", "invariant");
    [Fact] public async Task TzUtc_CultureDeDe_MatchesReferenceTable() => await AssertCellMatchesReferenceTable("UTC", "de-DE");
    [Fact] public async Task TzUtc_CultureItIt_MatchesReferenceTable() => await AssertCellMatchesReferenceTable("UTC", "it-IT");
    [Fact] public async Task TzLosAngeles_CultureInvariant_MatchesReferenceTable() => await AssertCellMatchesReferenceTable("America/Los_Angeles", "invariant");
    [Fact] public async Task TzLosAngeles_CultureDeDe_MatchesReferenceTable() => await AssertCellMatchesReferenceTable("America/Los_Angeles", "de-DE");
    [Fact] public async Task TzLosAngeles_CultureItIt_MatchesReferenceTable() => await AssertCellMatchesReferenceTable("America/Los_Angeles", "it-IT");

    // ── Contract guard ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Running the same cell twice must yield byte-identical stdout — guards against accidental nondeterminism (unordered collections, environment echo).</summary>
    [Fact] public async Task Runner_OutputContract_DeterministicAcrossRuns_SameBytes()
    {
        var first = await RunCellRaw("UTC", "invariant");
        var second = await RunCellRaw("UTC", "invariant");

        Assert.Equal(first, second);   // byte-identical raw stdout, not just line-equivalent
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task AssertCellMatchesReferenceTable(string tz, string culture)
    {
        var expected = TzCellReferenceTable.ExpectedLines;
        var raw = await RunCellRaw(tz, culture);
        var actual = ToLines(raw);

        if (actual.SequenceEqual(expected)) return;   // exact order + byte-for-byte — done

        Assert.Fail($"Cell TZ={tz} culture={culture} deviates from the reference table " +
            $"(raw stdout length {raw.Length}, lines {actual.Count}, expected {expected.Length}):\n{BuildDiff(expected, actual)}");
    }

    /// <summary>
    /// Launches one matrix cell as a child process and returns its raw stdout (UTF-8). The dotnet executable is resolved from
    /// DOTNET_HOST_PATH (set by the .NET host in both local runs and CI), so cells run on the exact SDK/runtime of the current
    /// test run. TZ is the only environment mutation — it applies to the child's whole lifetime, which is what makes
    /// TimeZoneInfo.Local reflect the cell (coordinator decision 1).
    /// </summary>
    private static async Task<string> RunCellRaw(string tz, string culture)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";   // fallback: PATH on the same machine
        var runnerDll = Path.Combine(AppContext.BaseDirectory, "TzCellRunner.dll");
        if (!File.Exists(runnerDll))
            throw new FileNotFoundException($"TzCellRunner.dll was not copied next to the test assembly (check the ProjectReference in NewsQuack.App.Tests.csproj): {runnerDll}");

        var startInfo = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,   // the runner writes UTF-8 per its output contract
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(runnerDll);
        startInfo.ArgumentList.Add(culture);
        startInfo.Environment["TZ"] = tz;   // the cell's timezone for the child's whole lifetime — no other env mutation

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{dotnet} exec' for cell TZ={tz} culture={culture}.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(CellTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"TzCellRunner cell TZ={tz} culture={culture} did not exit within {CellTimeout.TotalSeconds}s.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"TzCellRunner cell TZ={tz} culture={culture} exited with code {process.ExitCode}. stderr:\n{stderr}");

        return stdout;
    }

    /// <summary>Splits raw stdout into lines: normalizes CRLF and drops exactly one trailing empty entry from the final newline — nothing else.</summary>
    private static List<string> ToLines(string raw)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[^1] == string.Empty)   // the final-newline artifact only
            Array.Resize(ref lines, lines.Length - 1);

        return [.. lines];
    }

    /// <summary>Line-by-line diff for failure messages — makes a cell's deviation obvious at a glance.</summary>
    private static string BuildDiff(string[] expected, List<string> actual)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Math.Max(expected.Length, actual.Count); i++)
        {
            var e = i < expected.Length ? expected[i] : "(missing)";
            var a = i < actual.Count ? actual[i] : "(missing)";

            sb.AppendLine((e == a ? "  OK " : "DIFF ") + $"line {i + 1}:\n       expected: {e}\n       actual:   {a}");
        }

        return sb.ToString().TrimEnd();
    }
}
