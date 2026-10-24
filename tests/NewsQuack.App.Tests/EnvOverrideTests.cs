using NewsQuack.App.Configuration;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-003: env var value overrides — NEWSQUACK_ prefix, "__" section separator, env wins over file, empty values are "not set".</summary>
public class EnvOverrideTests
{
    [Fact]
    public void EnvOverride_WebhookUrlFromEnv_WinsOverFileValue()
    {
        var json = ConfigTestHelper.Config(ConfigTestHelper.Instance("a"));   // file carries the standard webhook URL

        var env = new Dictionary<string, string?>
        {
            ["NEWSQUACK_INSTANCES__0__DISCORD__WEBHOOKURL"] = "https://discord.com/api/webhooks/999888777666555444/ZzYyXxWwVvUuTtSsRrQqPpOo",
        };

        var result = ConfigTestHelper.LoadWithEnv(json, env);

        Assert.True(result.IsSuccess, string.Join("\n", result.Errors));
        Assert.Equal(
            "https://discord.com/api/webhooks/999888777666555444/ZzYyXxWwVvUuTtSsRrQqPpOo",
            Require(result.Config?.Instances?[0]?.Discord?.WebhookUrl));
    }

    [Fact]
    public void EnvOverride_MinSendIntervalSecondsFromEnv_WinsOverFileValue()
    {
        // T-048: the new key binds through the standard .NET pipeline exactly like the other Discord keys — env wins over file, and an in-range file value passes validation.
        var discord = $$"""{ "WebhookUrl": "{{ConfigTestHelper.WebhookUrl}}", "MinSendIntervalSeconds": 5 }""";
        var json = ConfigTestHelper.Config(ConfigTestHelper.Instance("a", 15, discord: discord));

        var env = new Dictionary<string, string?>
        {
            ["NEWSQUACK_INSTANCES__0__DISCORD__MINSENDINTERVALSECONDS"] = "7",
        };

        var result = ConfigTestHelper.LoadWithEnv(json, env);

        Assert.True(result.IsSuccess, string.Join("\n", result.Errors));

        var interval = result.Config?.Instances?[0]?.Discord?.MinSendIntervalSeconds;
        Assert.NotNull(interval);
        // non-null here: Assert.NotNull throws otherwise (xUnit v2's NotNull returns void, so the flow state needs this).
        Assert.Equal(7, interval!.Value);   // env value wins over the file value (5)
    }

    [Fact]
    public void EnvOverride_InvalidValueFromEnv_FailsValidationWithFriendlyMessage()
    {
        // env-supplied values go through the same total validator as file values: interval -1 → range error, wrong-scheme webhook → pattern error (masked)
        var json = ConfigTestHelper.Config(ConfigTestHelper.Instance("a"));

        var env = new Dictionary<string, string?>
        {
            ["NEWSQUACK_INSTANCES__0__CHECKINTERVALMINUTES"] = "-1",
            ["NEWSQUACK_INSTANCES__0__DISCORD__WEBHOOKURL"] = "http://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWx",
        };

        var result = ConfigTestHelper.LoadWithEnv(json, env);

        Assert.False(result.IsSuccess);
        Assert.Equal(2, result.Errors.Count);   // both violations in one pass — env values validated identically to file values
        Assert.Contains(result.Errors, m => m.StartsWith("Instances[0].CheckIntervalMinutes") && m.Contains("between 1 and 1440"));

        var webhookError = Require(result.Errors.Single(m => m.StartsWith("Instances[0].Discord.WebhookUrl")));
        Assert.Contains("…UvWx", webhookError);                       // masked echo, not the full token
        Assert.DoesNotContain("AbCdEfGhIjKlMnOpQrStUvWx", webhookError);

        foreach (var message in result.Errors)
            Assert.DoesNotContain("at System.", message);             // no stack trace lines — exit-1 style friendly errors
    }

    [Fact]
    public void EnvOverride_EmptyStringValue_TreatedAsNotSet_FileValueWins()
    {
        var json = $$"""{ "Instances": [ {{ConfigTestHelper.Instance("a")}} ], "Health": { "Port": 9090 } }""";

        var env = new Dictionary<string, string?>
        {
            ["NEWSQUACK_INSTANCES__0__DISCORD__WEBHOOKURL"] = "",
            ["NEWSQUACK_INSTANCES__0__CHECKINTERVALMINUTES"] = "",
            ["NEWSQUACK_HEALTH__PORT"] = "",   // file says 9090 — must win over the empty env value
        };

        var result = ConfigTestHelper.LoadWithEnv(json, env);

        Assert.True(result.IsSuccess, string.Join("\n", result.Errors));

        var instance = Require(result.Config?.Instances?[0]);
        Assert.Equal(ConfigTestHelper.WebhookUrl, Require(instance.Discord?.WebhookUrl));   // file value wins

        var interval = instance.CheckIntervalMinutes;
        Assert.NotNull(interval);
        // non-null here: Assert.NotNull throws otherwise (xUnit v2's NotNull returns void, so the flow state needs this).
        Assert.Equal(15, interval!.Value);                                                   // file value wins

        var health = Require(result.Config?.Health);
        Assert.Equal(9090, health.Port);                                                     // file value wins (not the schema default)
    }

    [Fact]
    public void EnvOverride_GlobalKey_HealthPort_WinsOverFile()
    {
        var json = $$"""{ "Instances": [ {{ConfigTestHelper.Instance("a")}} ], "Health": { "Port": 8081 } }""";

        var env = new Dictionary<string, string?>
        {
            ["NEWSQUACK_HEALTH__PORT"] = "9090",
        };

        var result = ConfigTestHelper.LoadWithEnv(json, env);

        Assert.True(result.IsSuccess, string.Join("\n", result.Errors));

        var health = Require(result.Config?.Health);
        Assert.Equal(9090, health.Port);
    }

    [Fact]
    public void EnvOverride_WrongTypeValueFromEnv_FriendlyErrorNotCrash()
    {
        // e.g. NEWSQUACK_HEALTH__PORT=abc — must surface as a friendly error naming the config path, not a crash/stack trace
        var json = ConfigTestHelper.Config(ConfigTestHelper.Instance("a"));

        var env = new Dictionary<string, string?>
        {
            ["NEWSQUACK_HEALTH__PORT"] = "abc",
        };

        var result = ConfigTestHelper.LoadWithEnv(json, env);

        Assert.False(result.IsSuccess);
        Assert.Single(result.Errors);
        var message = result.Errors[0];
        Assert.Contains("Health:Port", message, StringComparison.OrdinalIgnoreCase);   // names the exact config path
        Assert.DoesNotContain("at System.", message);                                 // no stack trace lines
    }

    [Fact]
    public void EnvOverride_IndexBeyondFileInstances_CreatesSparseInstanceWithFriendlyErrors()
    {
        // Index-based addressing: env index 1 beyond the file's single instance creates a sparse second instance —
        // reported by index with its missing required keys, no crash.
        var json = ConfigTestHelper.Config(ConfigTestHelper.Instance("a"));

        var env = new Dictionary<string, string?>
        {
            ["NEWSQUACK_INSTANCES__1__DISCORD__WEBHOOKURL"] = "https://discord.com/api/webhooks/999888777666555444/ZzYyXxWwVvUuTtSsRrQqPpOo",
        };

        var result = ConfigTestHelper.LoadWithEnv(json, env);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, m => m.StartsWith("Instances[1].Id"));
        Assert.Contains(result.Errors, m => m.StartsWith("Instances[1].CheckIntervalMinutes"));
        Assert.Contains(result.Errors, m => m.StartsWith("Instances[1].Feeds"));
    }

    private static T Require<T>(T? value) where T : class
    {
        Assert.NotNull(value);
        // non-null here: Assert.NotNull throws otherwise (xUnit v2's NotNull returns void, so the flow state needs this).
        return value!;
    }
}
