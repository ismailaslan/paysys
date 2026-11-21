using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.Api.Diagnostics;
using Paysys.DAL.Persistence;
using Paysys.Shared.Audit;

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

        // The 100 most recent entries the caller caused (all entries for an admin).
        app.MapGet("/api/audit/entries", async (ClaimsPrincipal user, PaysysDbContext db) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var isAdmin = user.IsAdmin();

            var entries = await db.TransactionAuditLogs.AsNoTracking()
                .Where(e => isAdmin || e.ActorUserId == userId.Value)
                .OrderByDescending(e => e.TimestampUtc)
                .Take(100)
                .ToListAsync();

            // Account references show as names where the caller may see the account.
            var accountNames = await db.Accounts.AsNoTracking()
                .Where(a => isAdmin || a.OwnerId == userId.Value)
                .ToDictionaryAsync(a => a.Id.ToString(), a => a.Name);

            string? NameOf(string? reference) => reference is null ? null : accountNames.GetValueOrDefault(reference, reference);

            return Results.Ok(entries.Select(e => new AuditEntryResponse(
                e.TimestampUtc, e.ActionType, e.Result, e.Amount, e.Currency,
                NameOf(e.FromAccountRef), NameOf(e.ToAccountRef), e.CardTokenRef, e.FlagReason)));
        }).RequireAuthorization();

        return app;
    }
}
