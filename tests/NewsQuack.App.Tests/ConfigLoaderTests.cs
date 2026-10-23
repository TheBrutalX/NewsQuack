using NewsQuack.App.Configuration;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-002: config file discovery, reading and friendly error messages (no stack traces, exit contract owned by Program).</summary>
public class ConfigLoaderTests
{
    [Fact]
    public void ConfigValidation_MissingInstanceFileListsAllSearchedPathsAndDockerInstruction()
    {
        var searchPaths = ConfigLoader.GetSearchPaths(Array.Empty<string>(), () => null);

        var result = ConfigLoader.MissingFile(searchPaths);

        Assert.False(result.IsSuccess);
        Assert.Single(result.Errors);
        var message = result.Errors[0];
        Assert.Contains("./newsquack.json", message);
        Assert.Contains("/config/newsquack.json", message);
        Assert.Contains("-v /host/path/newsquack.json:/config/newsquack.json", message);
        Assert.Contains("NEWSQUACK_CONFIG", message);
    }

    [Fact]
    public void ConfigValidation_CorruptJson_ReportsParseErrorWithoutStackTrace()
    {
        var (ok, messages) = ConfigTestHelper.Run("""{ "Instances": [ {""");

        Assert.False(ok);
        Assert.Single(messages);
        var message = messages[0];
        Assert.Contains("not valid JSON", message);
        Assert.DoesNotContain("at System.", message);          // no stack trace lines
        Assert.DoesNotContain("\n   at ", message);            // no stack trace lines
        Assert.DoesNotContain("Exception", message);           // no exception type names
    }

    [Fact]
    public void ConfigLoader_OmittedOptionalSections_ReturnSchemaDefaults()
    {
        var json = $$"""{ "Instances": [ {{ConfigTestHelper.Instance("a")}} ] }""";

        var result = ConfigTestHelper.Load(json);

        Assert.True(result.IsSuccess, string.Join("\n", result.Errors));

        var http = Require(result.Config?.Http);
        Assert.Equal(30, http.FeedTimeoutSeconds);
        Assert.Equal(15, http.DiscordTimeoutSeconds);
        Assert.Equal(15, http.ImageTimeoutSeconds);
        Assert.False(http.BlockPrivateNetworkRedirects);

        var health = Require(result.Config?.Health);
        Assert.True(health.Enabled);
        Assert.Equal(8081, health.Port);

        var logging = Require(result.Config?.Logging);
        Assert.Equal("Information", logging.Level);

        var instance = Require(result.Config?.Instances?[0]);
        var discord = Require(instance.Discord);
        Assert.Equal("embed", discord.Mode);
    }

    [Fact]
    public void ConfigDiscovery_CliArgBeatsEnvVar_ReturnsCliArgPath()
    {
        var dir = MakeTempDir();

        try
        {
            var cliFile = WriteFile(dir, "cli.json");
            var envFile = WriteFile(dir, "env.json");

            var searchPaths = ConfigLoader.GetSearchPaths([cliFile], () => envFile);

            Assert.Equal([cliFile, envFile, "./newsquack.json", "/config/newsquack.json"], searchPaths);
            Assert.Equal(cliFile, ConfigLoader.FindFirstExisting(searchPaths));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ConfigDiscovery_EnvVarBeatsCwd_ReturnsEnvVarPath()
    {
        var dir = MakeTempDir();

        try
        {
            var envFile = WriteFile(dir, "env.json");
            WriteFile(dir, "newsquack.json");   // ./newsquack.json exists in the CWD below — must lose to the env var

            RunInDirectory(dir, () =>
            {
                var searchPaths = ConfigLoader.GetSearchPaths(Array.Empty<string>(), () => envFile);
                Assert.Equal(envFile, ConfigLoader.FindFirstExisting(searchPaths));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ConfigDiscovery_CwdBeatsContainerPath_ReturnsCwdPath()
    {
        // /config is not writable in CI — skip this level when the container path happens to exist.
        if (File.Exists(ConfigLoader.ContainerConfigPath))
            return;

        var dir = MakeTempDir();

        try
        {
            WriteFile(dir, "newsquack.json");

            RunInDirectory(dir, () =>
            {
                var searchPaths = ConfigLoader.GetSearchPaths(Array.Empty<string>(), () => null);
                Assert.Equal("./newsquack.json", ConfigLoader.FindFirstExisting(searchPaths));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ConfigDiscovery_WhitespaceCliArgAndEnvVar_Ignored()
    {
        var searchPaths = ConfigLoader.GetSearchPaths(["   "], () => "  ");

        Assert.Equal(["./newsquack.json", "/config/newsquack.json"], searchPaths);
    }

    private static T Require<T>(T? value) where T : class
    {
        Assert.NotNull(value);
        // non-null here: Assert.NotNull throws otherwise (xUnit v2's NotNull returns void, so the flow state needs this).
        return value!;
    }

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"newsquack-t002-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteFile(string dir, string fileName)
    {
        var file = Path.Combine(dir, fileName);
        File.WriteAllText(file, "{}");
        return file;
    }

    /// <summary>Runs an action with the process working directory changed (restored on exit — other test classes use absolute paths only).</summary>
    private static void RunInDirectory(string dir, Action action)
    {
        var previous = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(dir);
            action();
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }
}
