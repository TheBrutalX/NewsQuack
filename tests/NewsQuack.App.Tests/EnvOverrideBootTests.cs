using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-003 boot integration: the built executable with real NEWSQUACK_* env vars (the full AddEnvironmentVariables path, end-to-end).</summary>
public class EnvOverrideBootTests
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void EnvOverride_WebhookFromEnv_ProcessStartsAndKeepsRunning()
    {
        var (dotnet, appAssembly) = AppBootHelper.FindApp();
        if (dotnet is null || appAssembly is null) return;   // no dotnet on PATH or no App build output — cannot run end-to-end

        var dir = AppBootHelper.MakeTempDir("newsquack-t003-envboot");

        try
        {
            // The file carries a wrong-scheme (invalid) webhook; the env var supplies a valid one.
            // The process starting at all proves the env value won over the file value end-to-end.
            const string fileWebhook = "http://discord.com/api/webhooks/123456789012345678/FileTokenAbCd";
            var discord = $$"""{ "WebhookUrl": "{{fileWebhook}}" }""";
            var json = $$"""{ "Instances": [ {{ConfigTestHelper.Instance("main", 15, discord: discord)}} ] }""";
            AppBootHelper.WriteFile(dir, "newsquack.json", json);

            using var boot = AppBootHelper.StartApp(dotnet, appAssembly, [], psi =>
            {
                psi.WorkingDirectory = dir;
                psi.Environment["NEWSQUACK_INSTANCES__0__DISCORD__WEBHOOKURL"] = ConfigTestHelper.WebhookUrl;
            });

            Assert.False(boot.Process.WaitForExit(10_000), "host exited right after startup — expected it to keep running (the env override should have won over the file value)");
            boot.Process.Kill(entireProcessTree: true);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task EnvOverride_InvalidWebhookFromEnv_Exits1WithMaskedUrlOnly()
    {
        var (dotnet, appAssembly) = AppBootHelper.FindApp();
        if (dotnet is null || appAssembly is null) return;   // no dotnet on PATH or no App build output — cannot run end-to-end

        var dir = AppBootHelper.MakeTempDir("newsquack-t003-envboot");

        try
        {
            // The file is fully valid; the env var overrides the webhook with a wrong-scheme URL carrying a secret token.
            const string envWebhook = "http://discord.com/api/webhooks/123456789012345678/SuperSecretTokenAbCd";
            AppBootHelper.WriteFile(dir, "newsquack.json", $$"""{ "Instances": [ {{ConfigTestHelper.Instance("main", 15)}} ] }""");

            using var boot = AppBootHelper.StartApp(dotnet, appAssembly, [], psi =>
            {
                psi.WorkingDirectory = dir;
                psi.Environment["NEWSQUACK_INSTANCES__0__DISCORD__WEBHOOKURL"] = envWebhook;
            });

            Assert.True(boot.Process.WaitForExit((int)ExitTimeout.TotalMilliseconds), "app did not exit within the timeout");
            Assert.Equal(1, boot.Process.ExitCode);

            var stdout = await boot.Stdout;
            var stderr = await boot.Stderr;
            Assert.Contains("Instances[0].Discord.WebhookUrl", stderr);
            Assert.Contains("…AbCd", stderr);                              // masked form in the error message
            Assert.DoesNotContain("SuperSecretTokenAbCd", stdout + stderr);   // full token never printed
            Assert.DoesNotContain("at System.", stdout + stderr);           // no stack trace on the exit path
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
