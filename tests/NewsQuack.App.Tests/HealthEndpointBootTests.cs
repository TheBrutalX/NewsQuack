using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-030 boot integration: the health listener on a free port (passed via NEWSQUACK_HEALTH__PORT, T-003's env override), the frozen endpoint shapes,
/// Health.Enabled=false leaving no HTTP listener at all, and the friendly exit-1 contract when the port is already in use.
/// </summary>
public class HealthEndpointBootTests
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task HealthZ_ValidConfig_Returns200OkJson()
    {
        var (dotnet, appAssembly) = AppBootHelper.FindApp();
        if (dotnet is null || appAssembly is null) return;   // no dotnet on PATH or no App build output — cannot run end-to-end

        var dir = AppBootHelper.MakeTempDir("newsquack-t030-health");
        var port = AppBootHelper.GetFreePort();

        try
        {
            var json = $$"""{ "Instances": [ {{ConfigTestHelper.Instance("main", 15)}} ] }""";
            var configFile = AppBootHelper.WriteFile(dir, "newsquack.json", json);

            using var boot = StartAppOnHealthPort(dotnet, appAssembly, configFile, port);

            try
            {
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
                await WaitForListenerAsync(boot, http, port);

                using var response = await http.GetAsync("/healthz");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // always 200 — the process is alive
                Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType ?? "");   // frozen: application/json

                var body = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                Assert.Single(root.EnumerateObject());            // frozen shape: exactly {"status":"ok"} — no extra fields
                Assert.Equal("ok", root.GetProperty("status").GetString());

                using var notFound = await http.GetAsync("/nope");    // any other path → default 404 (no catch-all mapping)
                Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
            }
            finally
            {
                boot.Process.Kill(entireProcessTree: true);   // the app keeps running — stop it explicitly (same pattern as the T-004 boot tests)
            }

            var stderr = await boot.Stderr;
            Assert.DoesNotContain("at System.", stderr);   // no stack trace on a successful boot
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Readyz_NoRequirementsRegistered_Returns200Ready()
    {
        var (dotnet, appAssembly) = AppBootHelper.FindApp();
        if (dotnet is null || appAssembly is null) return;   // no dotnet on PATH or no App build output — cannot run end-to-end

        var dir = AppBootHelper.MakeTempDir("newsquack-t030-health");
        var port = AppBootHelper.GetFreePort();

        try
        {
            var json = $$"""{ "Instances": [ {{ConfigTestHelper.Instance("main", 15)}} ] }""";
            var configFile = AppBootHelper.WriteFile(dir, "newsquack.json", json);

            using var boot = StartAppOnHealthPort(dotnet, appAssembly, configFile, port);

            try
            {
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
                await WaitForListenerAsync(boot, http, port);

                // T-030 registers no requirements — /readyz passes as soon as the listener is up (config validity is implied).
                using var response = await http.GetAsync("/readyz");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType ?? "");

                var body = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                Assert.Single(root.EnumerateObject());            // frozen shape: exactly {"status":"ready"} — no extra fields
                Assert.Equal("ready", root.GetProperty("status").GetString());
            }
            finally
            {
                boot.Process.Kill(entireProcessTree: true);
            }

            var stderr = await boot.Stderr;
            Assert.DoesNotContain("at System.", stderr);   // no stack trace on a successful boot
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Health_Disabled_ReturnsNoListener()
    {
        var (dotnet, appAssembly) = AppBootHelper.FindApp();
        if (dotnet is null || appAssembly is null) return;   // no dotnet on PATH or no App build output — cannot run end-to-end

        var dir = AppBootHelper.MakeTempDir("newsquack-t030-health");
        var port = AppBootHelper.GetFreePort();

        try
        {
            var json = $$"""{ "Instances": [ {{ConfigTestHelper.Instance("main", 15)}} ] }""";
            var configFile = AppBootHelper.WriteFile(dir, "newsquack.json", json);

            using var boot = AppBootHelper.StartApp(dotnet, appAssembly, [configFile], psi =>
            {
                psi.Environment["NEWSQUACK_HEALTH__PORT"] = port.ToString(CultureInfo.InvariantCulture);
                psi.Environment["NEWSQUACK_HEALTH__ENABLED"] = "false";   // T-003 env override — the listener must not come up at all
            });

            try
            {
                // The process stays alive — Health.Enabled=false removes only the listener, not the host. If it did exit, surface why (e.g. lost a port race).
                if (boot.Process.WaitForExit((int)StartTimeout.TotalMilliseconds))
                {
                    var earlyOut = await boot.Stdout;   // process gone → pipes closed, these complete immediately
                    var earlyErr = await boot.Stderr;
                    Assert.Fail($"app exited right after startup — expected it to keep running (exit code {boot.Process.ExitCode}, stderr: {Truncate(earlyErr)}, stdout: {Truncate(earlyOut)})");
                }

                // And nothing is listening on that port: TCP connect is refused while the process is alive.
                using var client = new TcpClient();
                await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
            }
            finally
            {
                boot.Process.Kill(entireProcessTree: true);   // the app keeps running — stop it explicitly (same pattern as the T-004 boot tests)
            }

            // It really booted into the (generic) host — JSON lifecycle line on stdout, no stack trace anywhere.
            var stdout = await boot.Stdout;
            var stderr = await boot.Stderr;
            Assert.Contains("Microsoft.Hosting.Lifetime", stdout);
            Assert.DoesNotContain("at System.", stdout + stderr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Health_PortInUse_Exits1WithFriendlyMessageNoStackTrace()
    {
        var (dotnet, appAssembly) = AppBootHelper.FindApp();
        if (dotnet is null || appAssembly is null) return;   // no dotnet on PATH or no App build output — cannot run end-to-end

        var dir = AppBootHelper.MakeTempDir("newsquack-t030-health");
        var port = AppBootHelper.GetFreePort();

        try
        {
            // Occupy the port in-test before the app starts: Kestrel must fail to bind.
            using var blocker = new TcpListener(IPAddress.Any, port);
            blocker.Start();

            var json = $$"""{ "Instances": [ {{ConfigTestHelper.Instance("main", 15)}} ] }""";
            var configFile = AppBootHelper.WriteFile(dir, "newsquack.json", json);

            using var boot = StartAppOnHealthPort(dotnet, appAssembly, configFile, port);

            Assert.True(boot.Process.WaitForExit((int)StartTimeout.TotalMilliseconds), "app did not exit within the timeout — expected a bind failure");
            Assert.Equal(1, boot.Process.ExitCode);

            var stdout = await boot.Stdout;
            var stderr = await boot.Stderr;

            // Friendly one-liner on stderr naming the port…
            Assert.Contains(port.ToString(CultureInfo.InvariantCulture), stderr);
            // …and no stack trace in it (no "at System." frames). stdout keeps T-004's structured JSON error line — that is where the exception belongs.
            Assert.DoesNotContain("at System.", stderr);

            // Every stdout line is still a valid JSON object, even on this failure path (frozen T-004 format).
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var doc = JsonDocument.Parse(line);
                Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);   // the blocker disposes with the try block; the app already exited on its own
        }
    }

    /// <summary>Starts the app with NEWSQUACK_HEALTH__PORT pointing at the given port (T-003's env override).</summary>
    private static AppBoot StartAppOnHealthPort(string dotnet, string appAssembly, string configFile, int port) =>
        AppBootHelper.StartApp(dotnet, appAssembly, [configFile], psi => psi.Environment["NEWSQUACK_HEALTH__PORT"] = port.ToString(CultureInfo.InvariantCulture));

    /// <summary>Polls /healthz until Kestrel answers (any response means the listener is up — it always returns 200).</summary>
    private static async Task WaitForListenerAsync(AppBoot boot, HttpClient http, int port)
    {
        var deadline = DateTimeOffset.UtcNow + StartTimeout;   // UTC — no local time in tests either

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync("/healthz");   // 200 — the process is alive and answering
                return;
            }
            catch (HttpRequestException)   // connection refused — the process is still starting, keep polling
            {
                await Task.Delay(100, CancellationToken.None);   // bounded wait loop with an explicit token (no unsupervised delay)
            }
        }

        // Diagnostic failure: if the app already exited (e.g. lost a port race to a parallel test), surface why — otherwise it is alive but not answering.
        if (boot.Process.HasExited)
        {
            var stdout = await boot.Stdout;   // process gone → pipes closed, these complete immediately
            var stderr = await boot.Stderr;
            Assert.Fail($"health listener on port {port} never came up — the app exited with code {boot.Process.ExitCode} (stderr: {Truncate(stderr)}, stdout: {Truncate(stdout)})");
        }

        Assert.Fail($"health listener on port {port} did not come up within {StartTimeout.TotalSeconds:0}s");
    }

    /// <summary>Keeps failure messages short — full output is usually far longer than a test-report line.</summary>
    private static string Truncate(string s) => s.Length <= 400 ? s : $"{s[..400]}…";
}
