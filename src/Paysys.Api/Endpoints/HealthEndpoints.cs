using Paysys.DAL.Persistence;

namespace Paysys.Api.Endpoints;

// Anonymous on purpose: an uptime pinger and a hosting platform's probe carry no token.
// Neither endpoint returns anything but a fixed status word - no versions, no counts, no
// connection details.
public static class HealthEndpoints
{
    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        // Liveness: the process is up and serving requests. Does not touch the database,
        // so a database outage does not make a host restart a healthy API.
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        // Readiness: the API can reach its database.
        app.MapGet("/readyz", async (PaysysDbContext db, CancellationToken cancellationToken) =>
            await db.Database.CanConnectAsync(cancellationToken)
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new { status = "database unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

        return app;
    }
}
