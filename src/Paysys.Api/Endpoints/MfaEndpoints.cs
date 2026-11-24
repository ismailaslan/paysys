using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.Shared.Profile;

namespace Paysys.Api.Endpoints;

// Two-step sign-in, set up and turned off from the profile (the sign-in itself is in AuthEndpoints). Set-up has two
// calls: start (a new secret, kept encrypted but not yet in force) and confirm (the person types a code from their app,
// which proves it works; only then is it on, and the recovery codes are shown once). Turning it off needs the
// password and a code. Wrong codes count toward the sign-in lockout, so a 6-digit code cannot be guessed freely.
public static class MfaEndpoints
{
    public static WebApplication MapMfaEndpoints(this WebApplication app)
    {
        app.MapPost("/api/profile/mfa/start", async (ClaimsPrincipal principal, PaysysDbContext db, MfaCrypto mfa) =>
        {
            var userId = principal.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId.Value);
            if (user is null)
                return Results.Unauthorized();

            if (user.TotpEnabledAt is not null)
                return Results.Json(new { message = "Two-step sign-in is already on." }, statusCode: StatusCodes.Status409Conflict);

            var secret = Totp.NewSecret();
            user.BeginTotp(mfa.Protect(secret));
            await db.SaveChangesAsync();

            var text = Totp.ToBase32(secret);
            var link = $"otpauth://totp/Paysys:{Uri.EscapeDataString(user.Username)}?secret={text}&issuer=Paysys&digits={Totp.Digits}&period={Totp.StepSeconds}";
            return Results.Ok(new MfaStartResponse(text, link));
        }).RequireAuthorization();

        app.MapPost("/api/profile/mfa/confirm", async (MfaCodeRequest request, ClaimsPrincipal principal, PaysysDbContext db, MfaCrypto mfa,
            LoginThrottle throttle, TransactionAuditLogService audit) =>
        {
            var userId = principal.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId.Value);
            if (user is null)
                return Results.Unauthorized();

            if (user.TotpEnabledAt is not null)
                return Results.Json(new { message = "Two-step sign-in is already on." }, statusCode: StatusCodes.Status409Conflict);

            var secret = user.TotpSecret is null ? null : mfa.Unprotect(user.TotpSecret);
            if (secret is null)
                return Results.Json(new { message = "Start the set-up first." }, statusCode: StatusCodes.Status409Conflict);

            var key = LoginThrottle.Normalize(user.Username);
            var locked = throttle.GetLockRemaining(key);
            if (locked is not null)
                return new RetryAfterResult(locked.Value,
                    $"Too many failed attempts. Try again in {Math.Max(1, (int)Math.Ceiling(locked.Value.TotalSeconds))} seconds.");

            if (!Totp.TryVerify(secret, request.Code?.Replace(" ", ""), DateTimeOffset.UtcNow, null, out var step))
            {
                throttle.RecordFailure(key);
                return Results.Json(new { message = "That code is not right. Check the app and try again." }, statusCode: StatusCodes.Status400BadRequest);
            }

            throttle.RecordSuccess(key);
            var codes = MfaCrypto.NewRecoveryCodes();
            user.EnableTotp(codes.Select(MfaCrypto.HashRecoveryCode), step, DateTimeOffset.UtcNow);

            await audit.AppendAndSaveAsync(new NewAuditEntry(
                null, AuditActionTypes.MfaEnabled, null, null,
                null, null, null, null, "Success", null, userId.Value));
            return Results.Ok(new MfaEnabledResponse(codes));
        }).RequireAuthorization();

        app.MapPost("/api/profile/mfa/disable", async (MfaDisableRequest request, ClaimsPrincipal principal, PaysysDbContext db, MfaCrypto mfa,
            UserAuthenticator authenticator, LoginThrottle throttle, TransactionAuditLogService audit) =>
        {
            var userId = principal.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId.Value);
            if (user is null)
                return Results.Unauthorized();

            if (user.TotpEnabledAt is null)
                return Results.Json(new { message = "Two-step sign-in is not on." }, statusCode: StatusCodes.Status409Conflict);

            var key = LoginThrottle.Normalize(user.Username);
            var locked = throttle.GetLockRemaining(key);
            if (locked is not null)
                return new RetryAfterResult(locked.Value,
                    $"Too many failed attempts. Try again in {Math.Max(1, (int)Math.Ceiling(locked.Value.TotalSeconds))} seconds.");

            if (await authenticator.AuthenticateAsync(user.Username, request.Password) is null)
            {
                throttle.RecordFailure(key);
                await audit.AppendAndSaveAsync(new NewAuditEntry(
                    null, AuditActionTypes.RejectedPassword, null, null,
                    null, null, null, null, AuditResults.Rejected, null, userId.Value));
                return Results.Json(new { message = "The password is wrong." }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (!VerifySecondFactor(user, request.Code, mfa, DateTimeOffset.UtcNow, out _))
            {
                throttle.RecordFailure(key);
                await audit.AppendAndSaveAsync(new NewAuditEntry(
                    null, AuditActionTypes.RejectedMfa, null, null,
                    null, null, null, null, AuditResults.Rejected, null, userId.Value));
                return Results.Json(new { message = "That code is not right." }, statusCode: StatusCodes.Status403Forbidden);
            }

            throttle.RecordSuccess(key);
            user.DisableTotp();
            await audit.AppendAndSaveAsync(new NewAuditEntry(
                null, AuditActionTypes.MfaDisabled, null, null,
                null, null, null, null, "Success", null, userId.Value));
            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }

    // Accepts a current authenticator code (not used before) or an unused recovery code. It changes the user (the used
    // step, or the used recovery code) but does not save: the caller saves, so the change and its audit entry go together.
    internal static bool VerifySecondFactor(User user, string? code, MfaCrypto mfa, DateTimeOffset now, out bool usedRecoveryCode)
    {
        usedRecoveryCode = false;
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var trimmed = code.Trim();
        var secret = user.TotpSecret is null ? null : mfa.Unprotect(user.TotpSecret);
        if (secret is not null && Totp.TryVerify(secret, trimmed.Replace(" ", ""), now, user.TotpLastStep, out var step))
        {
            user.RecordTotpStep(step);
            return true;
        }

        if (user.ConsumeRecoveryCode(MfaCrypto.HashRecoveryCode(trimmed)))
        {
            usedRecoveryCode = true;
            return true;
        }

        return false;
    }
}
