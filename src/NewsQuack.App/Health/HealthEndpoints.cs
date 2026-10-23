using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace NewsQuack.App.Health;

/// <summary>
/// Maps the two frozen health endpoints (T-030) on the lightweight Kestrel listener:
///   GET /healthz → 200 {"status":"ok"}                      — process is alive (always, while the listener answers)
///   GET /readyz  → 200 {"status":"ready"}                   — every registered requirement reported ready
///                → 503 {"status":"not_ready","missing":[...]} otherwise
/// Any other path gets Kestrel's default 404 (no catch-all mapping). Non-goals of T-030: no HTTPS, no auth, no metrics.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // The anonymous types serialize to exactly the frozen bodies (lowercase property names, JSON via Results.Json defaults).
        endpoints.MapGet("/healthz", () => Results.Json(new { status = "ok" }));

        endpoints.MapGet("/readyz", (ReadinessTracker tracker) =>
            tracker.IsReady
                ? Results.Json(new { status = "ready" })
                : Results.Json(new { status = "not_ready", missing = tracker.Missing }, statusCode: StatusCodes.Status503ServiceUnavailable));

        return endpoints;
    }
}
