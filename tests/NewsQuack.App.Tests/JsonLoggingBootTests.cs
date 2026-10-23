using System.Text.Json;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-004 boot integration: a valid config boots into structured JSON on stdout (frozen field names), Logging.Level is honored, and the T-002/T-003 stderr contract is unchanged.</summary>
public class JsonLoggingBootTests
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Boot_ValidConfig_LogsToStdoutAsJson()
    {
        var (dotnet, appAssembly) = AppBootHelper.FindApp();
        if (dotnet is null || appAssembly is null) return;   // no dotnet on PATH or no App build output — cannot run end-to-end

        var dir = AppBootHelper.MakeTempDir("newsquack-t004-boot");

        try
        {
            var json = $$"""{ "Instances": [ {{ConfigTestHelper.Instance("main", 15)}} ] }""";
            var configFile = AppBootHelper.WriteFile(dir, "newsquack.json", json);

            using var boot = AppBootHelper.StartApp(dotnet, appAssembly, [configFile]);

            Assert.False(boot.Process.WaitForExit((int)StartTimeout.TotalMilliseconds), "host exited right after startup — expected it to keep running");
            boot.Process.Kill(entireProcessTree: true);

            var stdout = await boot.Stdout;
            var stderr = await boot.Stderr;

            Assert.False(string.IsNullOrWhiteSpace(stdout), "expected JSON log lines on stdout after a successful boot");

            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var doc = JsonDocument.Parse(line);   // every line is valid JSON — the default console logger is gone
                var root = doc.RootElement;
                Assert.Equal(JsonValueKind.Object, root.ValueKind);

                foreach (var field in new[] { "@timestamp", "level", "logger", "message" })
                    Assert.True(root.TryGetProperty(field, out _), $"missing '{field}' in: {line}");

                var timestamp = root.GetProperty("@timestamp").GetString()!;
                Assert.EndsWith("Z", timestamp);   // ISO 8601 UTC regardless of the host's timezone
            }

            // The host lifecycle line proves our provider replaced the default console logger on stdout.
            Assert.Contains(stdout.Split('\n'), l => l.Contains("\"Microsoft.Hosting.Lifetime\""));

            // Boot stderr contract (T-002/T-003): human-readable only — no stack trace anywhere on a successful boot.
            Assert.DoesNotContain("at System.", stdout + stderr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Boot_ValidConfigWithLogLevelError_InformationLinesSuppressed()
    {
        var (dotnet, appAssembly) = AppBootHelper.FindApp();
        if (dotnet is null || appAssembly is null) return;   // no dotnet on PATH or no App build output — cannot run end-to-end

        var dir = AppBootHelper.MakeTempDir("newsquack-t004-boot");

        try
        {
            // Logging.Level from the schema must reach the host's standard level configuration — Information lines (incl. host lifecycle) disappear.
            var json = $$"""{ "Instances": [ {{ConfigTestHelper.Instance("main", 15)}} ], "Logging": { "Level": "Error" } }""";
            var configFile = AppBootHelper.WriteFile(dir, "newsquack.json", json);

            using var boot = AppBootHelper.StartApp(dotnet, appAssembly, [configFile]);

            Assert.False(boot.Process.WaitForExit((int)StartTimeout.TotalMilliseconds), "host exited right after startup — expected it to keep running");
            boot.Process.Kill(entireProcessTree: true);

            var stdout = await boot.Stdout;

            Assert.DoesNotContain("Microsoft.Hosting.Lifetime", stdout);   // its lines are Information level — filtered by Logging.Level
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var doc = JsonDocument.Parse(line);   // whatever survives the filter is still a valid JSON line
                var level = doc.RootElement.GetProperty("level").GetString();
                Assert.Contains(level, new[] { "Error", "Critical" });
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Boot_InvalidConfig_StderrStaysHumanReadableAndNoJsonOnStdout()
    {
        var (dotnet, appAssembly) = AppBootHelper.FindApp();
        if (dotnet is null || appAssembly is null) return;   // no dotnet on PATH or no App build output — cannot run end-to-end

        var dir = AppBootHelper.MakeTempDir("newsquack-t004-boot");

        try
        {
            var configFile = AppBootHelper.WriteFile(dir, "newsquack.json", """{ "Instances": [] }""");

            using var boot = AppBootHelper.StartApp(dotnet, appAssembly, [configFile]);
            Assert.True(boot.Process.WaitForExit((int)StartTimeout.TotalMilliseconds), "app did not exit within the timeout");
            Assert.Equal(1, boot.Process.ExitCode);

            var stdout = await boot.Stdout;
            var stderr = await boot.Stderr;

            // The failure path happens before the JSON pipeline exists: friendly human-readable stderr (T-002/T-003), nothing on stdout.
            Assert.Contains("Instances", stderr);
            Assert.Contains("at least one instance", stderr);
            Assert.DoesNotContain("at System.", stdout + stderr);
            Assert.True(string.IsNullOrWhiteSpace(stdout), $"the failure path exits before the JSON pipeline exists — nothing may be logged to stdout (got: {stdout})");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
