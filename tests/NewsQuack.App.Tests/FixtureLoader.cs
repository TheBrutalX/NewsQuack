using System.Text;
namespace NewsQuack.App.Tests;

/// <summary>
/// Resolves the shared feed fixtures under tests/fixtures/ relative to the solution root:
/// walks up from the test assembly location until Newsquack.sln is found, so the helper works
/// from any build output layout (Debug/Release, redirected output dirs, CI checkouts).
/// </summary>
public static class FixtureLoader
{
    private static string? _solutionRoot;

    /// <summary>Absolute path of the directory containing Newsquack.sln (cached after first resolution).</summary>
    public static string SolutionRoot
    {
        get
        {
            if (_solutionRoot is not null) return _solutionRoot;

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Newsquack.sln")))
                dir = dir.Parent;

            if (dir is null)
                throw new InvalidOperationException("Could not locate Newsquack.sln above the test assembly location.");

            _solutionRoot = dir.FullName;
            return _solutionRoot;
        }
    }

    /// <summary>Reads a fixture file from tests/fixtures/ as UTF-8 text.</summary>
    public static string Load(string fileName) =>
        File.ReadAllText(Path.Combine(SolutionRoot, "tests", "fixtures", fileName), Encoding.UTF8);
}
