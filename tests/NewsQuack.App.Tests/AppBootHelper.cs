using System.Diagnostics;
using NewsQuack.App.Configuration;

namespace NewsQuack.App.Tests;

/// <summary>A started app process with its output streams drained asynchronously (no pipe deadlock).</summary>
public sealed record AppBoot(Process Process, Task<string> Stdout, Task<string> Stderr) : IDisposable
{
    public void Dispose() => Process.Dispose();
}

/// <summary>Shared helpers for boot-level tests that run the built App executable (T-002 boot contract, reused by T-003).</summary>
internal static class AppBootHelper
{
    /// <summary>
    /// Starts "dotnet exec NewsQuack.App.dll [args]" with stdout/stderr drained asynchronously. All NEWSQUACK_* variables are removed
    /// from the inherited environment first — discovery and value overrides under test decide what is set (the configure action).
    /// </summary>
    public static AppBoot StartApp(string dotnet, string appAssembly, string[] args, Action<ProcessStartInfo>? configure = null)
    {
        var psi = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(appAssembly);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        // NEWSQUACK_CONFIG and every NEWSQUACK_* value override must not leak in from the test environment.
        var leaked = psi.Environment.Keys
            .Where(name => name.StartsWith(ConfigLoader.EnvironmentVariablePrefix, StringComparison.Ordinal))
            .ToList();
        foreach (var name in leaked)
            psi.Environment.Remove(name);

        configure?.Invoke(psi);

        var process = Process.Start(psi)!;   // non-null: Process.Start throws when the process cannot be started
        return new AppBoot(process, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
    }

    /// <summary>
    /// Locates the dotnet executable and NewsQuack.App.dll in the App project's own build output (same configuration as this test run).
    /// The app must run from its own directory: WireMock.Net (a test dependency) adds FrameworkReference Microsoft.AspNetCore.App,
    /// whose platform assemblies win over CopyLocal — so the test output dir does not contain all of the app's dependencies.
    /// </summary>
    public static (string? Dotnet, string? AppAssembly) FindApp()
    {
        var dotnet = FindDotnet();

        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);   // .../tests/NewsQuack.App.Tests/bin/<Config>/net10.0/
        var segments = baseDir.FullName.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var binIndex = Array.IndexOf(segments, "bin");
        if (binIndex < 0 || binIndex + 2 >= segments.Length) return (dotnet, null);
        var config = segments[binIndex + 1];

        var dir = baseDir;   // walk up to the solution root (same pattern as FixtureLoader)
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Newsquack.sln")))
            dir = dir.Parent;
        if (dir is null) return (dotnet, null);

        var appAssembly = Path.Combine(dir.FullName, "src", "NewsQuack.App", "bin", config, "net10.0", "NewsQuack.App.dll");
        return (dotnet, File.Exists(appAssembly) ? appAssembly : null);
    }

    public static string MakeTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string WriteFile(string dir, string fileName, string content)
    {
        var file = Path.Combine(dir, fileName);
        File.WriteAllText(file, content);
        return file;
    }

    private static string? FindDotnet()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (dotnetRoot is not null)
        {
            var candidate = Path.Combine(dotnetRoot, "dotnet" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            if (File.Exists(candidate)) return candidate;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "dotnet" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
