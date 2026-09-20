using Paysys.Api.Auth;
using Paysys.Api.Diagnostics;

namespace Paysys.Api.Endpoints;

public static class AuditEndpoints
{
    public static WebApplication MapAuditEndpoints(this WebApplication app)
    {
        // Read-only: reports the last scheduled check, it does not start one. Admin only -
        // it says whether the audit trail can be trusted.
        app.MapGet("/api/audit/integrity", (AuditChainStatus status) =>
        {
            var latest = status.Latest;
            return latest is null
                ? Results.Json(new { message = "No check has completed since the Api started." }, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(latest);
        }).RequireAuthorization(AuthPolicies.Admin);

        return app;
    }
}
