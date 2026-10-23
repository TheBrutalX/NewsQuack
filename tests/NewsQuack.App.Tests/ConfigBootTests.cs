using System.Globalization;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-002 boot integration: runs the built App executable and checks the exit-1 / keep-running contract end-to-end.</summary>
public class ConfigBootTests
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ConfigBoot_InvalidConfig_Exits1WithFriendlyStderrAndNoStackTrace()
    {
        var (dotnet, appAssembly) = AppBootHelper.FindApp();
        if (dotnet is null || appAssembly is null) return;   // no dotnet on PATH or no App build output — cannot run end-to-end

        var dir = AppBootHelper.MakeTempDir("newsquack-t002-boot");

        try
        {
            var configFile = AppBootHelper.WriteFile(dir, "newsquack.json", """{ "Instances": [] }""");

            using var boot = AppBootHelper.StartApp(dotnet, appAssembly, [configFile]);
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
        var (dotnet, appAssembly) = AppBootHelper.FindApp();
        if (dotnet is null || appAssembly is null) return;   // no dotnet on PATH or no App build output — cannot run end-to-end

        var dir = AppBootHelper.MakeTempDir("newsquack-t002-boot");      // empty working directory: no ./newsquack.json here

        try
        {
            using var boot = AppBootHelper.StartApp(dotnet, appAssembly, [], configure: psi => psi.WorkingDirectory = dir);

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
        var (dotnet, appAssembly) = AppBootHelper.FindApp();
        if (dotnet is null || appAssembly is null) return;   // no dotnet on PATH or no App build output — cannot run end-to-end

        var dir = AppBootHelper.MakeTempDir("newsquack-t002-boot");

        try
        {
            var json = $$"""{ "Instances": [ {{ConfigTestHelper.Instance("main", 15)}} ] }""";
            var configFile = AppBootHelper.WriteFile(dir, "newsquack.json", json);

            // T-030: the health listener is enabled by default — pin a unique port so parallel boot tests do not collide on 8081.
            var port = AppBootHelper.GetFreePort();
            using var boot = AppBootHelper.StartApp(dotnet, appAssembly, [configFile], psi => psi.Environment["NEWSQUACK_HEALTH__PORT"] = port.ToString(CultureInfo.InvariantCulture));

            Assert.False(boot.Process.WaitForExit(10_000), "host exited right after startup — expected it to keep running");
            boot.Process.Kill(entireProcessTree: true);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
