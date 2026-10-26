using Microsoft.Extensions.Logging;   // LogDebug/LogInformation/LogWarning extension methods on the bridge's M.E.L. loggers
using Serilog.Events;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-054: the EF Core noise cap (constraint 5) — proven through the production bridge exactly as Program.cs wires it: global minimum + conditional override + FromLogContext.</summary>
public class EfCoreLogCapTests
{
    private const string EfCategory = "Microsoft.EntityFrameworkCore.Database.Command";   // EF's command-log category — prefix-matched by the cap

    /// <summary>Mirrors Program.cs's Serilog wiring (global minimum + conditional EF cap + FromLogContext) into a line capture.</summary>
    private static SerilogLineCapture Capture(LogEventLevel minimum) => new(cfg =>
    {
        cfg.MinimumLevel.Is(minimum);

        if (minimum > LogEventLevel.Debug)   // Information/Warning/Error/Critical — cap EF Core noise; Debug/Trace: no override (exactly Program.cs's rule)
            cfg.MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning);

        cfg.Enrich.FromLogContext();
    });

    [Fact]
    public void EfCap_GlobalInformation_EfCoreDebugCommandLineSuppressed()
    {
        using var capture = Capture(LogEventLevel.Information);   // ⇒ override active (effective minimum Warning)

        capture.Logger(EfCategory).LogDebug("Executing db command");   // EF's command-log level — the chatty line the cap exists to hide

        Assert.Empty(capture.Lines());   // zero captured events — filtered by Serilog before it reaches any sink
    }

    [Fact]
    public void EfCap_GlobalInformation_NonEfCategoryStillLogsAtInformation()
    {
        using var capture = Capture(LogEventLevel.Information);   // control: the cap must not leak to other categories

        capture.Logger("NewsQuack.App.SomeWorker").LogInformation("worker tick");

        Assert.Single(capture.Lines());
    }

    [Fact]
    public void EfCap_GlobalDebug_EfCoreCommandLinePresent()
    {
        using var capture = Capture(LogEventLevel.Debug);   // ⇒ no override — EF follows the global level with no cap

        capture.Logger(EfCategory).LogDebug("Executing db command");

        Assert.Single(capture.Lines());
    }

    [Fact]
    public void EfCap_GlobalInformation_EfCoreWarningStillEmitted()
    {
        using var capture = Capture(LogEventLevel.Information);   // the cap is a floor at Warning, not stricter

        capture.Logger(EfCategory).LogWarning("db warning");

        Assert.Single(capture.Lines());
    }
}
