using System.Diagnostics;
using NewsQuack.App.Configuration;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-002 boot integration: runs the built App executable and checks the exit-1 / keep-running contract end-to-end.</summary>
public class ConfigBootTests
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ConfigBoot_InvalidConfig_Exits1WithFriendlyStderrAndNoStackTrace()
    {
        var (dotnet, appAssembly) = FindApp();
        if (dotnet is null || appAssembly is null) return;   // no dotnet on PATH or no App build output — cannot run end-to-end

        var dir = MakeTempDir();

        try
        {
            var configFile = WriteFile(dir, "newsquack.json", """{ "Instances": [] }""");

            using var boot = StartApp(dotnet, appAssembly, [configFile]);
            Assert.True(boot.Process.WaitForExit((int)ExitTimeout.TotalMilliseconds), "app did not exit within the timeout");
            Assert.Equal(1, boot.Process.ExitCode);

            var stdout = await boot.Stdout;
            var stderr = await boot.Stderr;
            Assert.Contains("Instances", stderr);
            Assert.Contains("at least one instance", stderr);
            Assert.DoesNotContain("at System.", stdout + stderr);   // no stack trace on the exit path
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigBoot_MissingConfigFile_Exits1ListingSearchedPathsAndDockerInstruction()
    {
        var (dotnet, appAssembly) = FindApp();
        if (dotnet is null || appAssembly is null) return;   // no dotnet on PATH or no App build output — cannot run end-to-end

        var dir = MakeTempDir();      // empty working directory: no ./newsquack.json here

        try
        {
            using var boot = StartApp(dotnet, appAssembly, [], configure: psi => psi.WorkingDirectory = dir);

            Assert.True(boot.Process.WaitForExit((int)ExitTimeout.TotalMilliseconds), "app did not exit within the timeout");
            Assert.Equal(1, boot.Process.ExitCode);

            var stdout = await boot.Stdout;
            var stderr = await boot.Stderr;
            Assert.Contains("No config file found", stderr);
            Assert.Contains("-v /host/path/newsquack.json:/config/newsquack.json", stderr);
            Assert.DoesNotContain("at System.", stdout + stderr);   // no stack trace on the exit path
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ConfigBoot_ValidConfig_HostStartsAndKeepsRunning()
    {
        var (dotnet, appAssembly) = FindApp();
        if (dotnet is null || appAssembly is null) return;   // no dotnet on PATH or no App build output — cannot run end-to-end

        var dir = MakeTempDir();

        try
        {
            var json = $$"""{ "Instances": [ {{ConfigTestHelper.Instance("main", 15)}} ] }""";
            var configFile = WriteFile(dir, "newsquack.json", json);

            using var boot = StartApp(dotnet, appAssembly, [configFile]);

            Assert.False(boot.Process.WaitForExit(10_000), "host exited right after startup — expected it to keep running");
            boot.Process.Kill(entireProcessTree: true);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed record AppBoot(Process Process, Task<string> Stdout, Task<string> Stderr) : IDisposable
    {
        public void Dispose() => Process.Dispose();
    }

    /// <summary>Starts "dotnet exec NewsQuack.App.dll [args]" with stdout/stderr drained asynchronously (no pipe deadlock).</summary>
    private static AppBoot StartApp(string dotnet, string appAssembly, string[] args, Action<ProcessStartInfo>? configure = null)
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

        // NEWSQUACK_CONFIG must not leak in from the test environment — discovery under test decides.
        psi.Environment.Remove(ConfigLoader.ConfigEnvironmentVariable);
        configure?.Invoke(psi);

        var process = Process.Start(psi)!;   // non-null: Process.Start throws when the process cannot be started
        return new AppBoot(process, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
    }

    /// <summary>
    /// Locates the dotnet executable and NewsQuack.App.dll in the App project's own build output (same configuration as this test run).
    /// The app must run from its own directory: WireMock.Net (a test dependency) adds FrameworkReference Microsoft.AspNetCore.App,
    /// whose platform assemblies win over CopyLocal — so the test output dir does not contain all of the app's dependencies.
    /// </summary>
    private static (string? Dotnet, string? AppAssembly) FindApp()
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

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"newsquack-t002-boot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteFile(string dir, string fileName, string content)
    {
        var file = Path.Combine(dir, fileName);
        File.WriteAllText(file, content);
        return file;
    }
}
