using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.BLL.Services;
using Paysys.DAL.Persistence;
using Paysys.Shared.Auth;

namespace Paysys.Api.Endpoints;

public static class AuthEndpoints
{
    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        // Deliberately anonymous - it is how a caller gets a token in the first place.
        app.MapPost("/api/auth/login", async (LoginRequest request, UserAuthenticator authenticator, TokenService tokens,
            LoginThrottle throttle, MfaCrypto mfa, AuthOptions authOptions, ILoggerFactory loggerFactory) =>
        {
            var log = loggerFactory.CreateLogger("Paysys.Api.Auth");
            var key = LoginThrottle.Normalize(request.Username);

            // Checked before the user lookup and the password hash, so a locked username costs
            // no database round trip and no CPU. Even a correct password is refused while locked.
            var locked = throttle.GetLockRemaining(key);
            if (locked is not null)
            {
                log.LogWarning("Login refused for {Username}: locked for another {Seconds} s", key, (int)locked.Value.TotalSeconds);
                return new RetryAfterResult(locked.Value,
                    $"Too many failed sign-in attempts. Try again in {Math.Max(1, (int)Math.Ceiling(locked.Value.TotalSeconds))} seconds.");
            }

            var user = await authenticator.AuthenticateAsync(request.Username, request.Password);

            // The right password at the wrong door (a merchant on the Individual tab, say) is refused
            // exactly like a wrong password, so the response does not reveal what kind the account is.
            if (user is not null && !RoleMatchesKind(user.Role, request.AccountKind))
                user = null;

            if (user is null)
            {
                var lockedFor = throttle.RecordFailure(key);
                if (lockedFor is not null)
                    log.LogWarning("Login failed for {Username}; lockout threshold reached, locked for {Seconds} s", key, (int)lockedFor.Value.TotalSeconds);
                else
                    log.LogInformation("Login failed for {Username}", key);

                // Same message for "no such user" and "wrong password".
                return Results.Json(
                    new { message = "Invalid username or password." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            // Two-step sign-in is on: the password was right but no token is issued yet. The failure count is not
            // reset here, so wrong codes in the next step add up toward the same lockout.
            if (user.TotpEnabledAt is not null)
            {
                log.LogInformation("Password accepted for user {UserId}; a sign-in code is needed", user.Id);
                return Results.Json(new MfaRequiredResponse(true, mfa.CreateChallenge(user.Id, DateTimeOffset.UtcNow)),
                    statusCode: StatusCodes.Status202Accepted);
            }

            throttle.RecordSuccess(key);

            // This role must use two-step sign-in and has not set it up: the password was right, so the person gets a
            // token that can do the setup and nothing else. After the setup they sign in again, with a code.
            if (authOptions.RequiresTwoStep(user.Role))
            {
                log.LogInformation("Login accepted for user {UserId}; two-step sign-in must be set up first", user.Id);
                var (setupToken, setupExpiresAt) = tokens.Issue(user, setupOnly: true);
                return Results.Ok(new LoginResponse(setupToken, setupExpiresAt, user.Username, user.Role, TwoStepSetupRequired: true));
            }

            log.LogInformation("Login succeeded for user {UserId}", user.Id);

            var (token, expiresAt) = tokens.Issue(user);
            return Results.Ok(new LoginResponse(token, expiresAt, user.Username, user.Role));
        });

        // The second step of a sign-in with two-step sign-in on. Anonymous like the first step: the challenge from the
        // first step is what ties them together. A wrong code counts toward the same lockout as a wrong password.
        app.MapPost("/api/auth/login/mfa", async (MfaLoginRequest request, PaysysDbContext db, TokenService tokens, MfaCrypto mfa,
            LoginThrottle throttle, TransactionAuditLogService audit, ILoggerFactory loggerFactory) =>
        {
            var log = loggerFactory.CreateLogger("Paysys.Api.Auth");

            if (!mfa.TryReadChallenge(request.Challenge, DateTimeOffset.UtcNow, out var userId))
                return Results.Json(new { message = "The sign-in step expired. Sign in again." }, statusCode: StatusCodes.Status401Unauthorized);

            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId);
            if (user is null || user.TotpEnabledAt is null)
                return Results.Json(new { message = "The sign-in step expired. Sign in again." }, statusCode: StatusCodes.Status401Unauthorized);

            var key = LoginThrottle.Normalize(user.Username);
            var locked = throttle.GetLockRemaining(key);
            if (locked is not null)
                return new RetryAfterResult(locked.Value,
                    $"Too many failed sign-in attempts. Try again in {Math.Max(1, (int)Math.Ceiling(locked.Value.TotalSeconds))} seconds.");

            if (!MfaEndpoints.VerifySecondFactor(user, request.Code, mfa, DateTimeOffset.UtcNow, out var usedRecoveryCode))
            {
                throttle.RecordFailure(key);
                log.LogInformation("Sign-in code rejected for user {UserId}", user.Id);
                await audit.AppendAndSaveAsync(new NewAuditEntry(
                    null, AuditActionTypes.RejectedMfa, null, null,
                    null, null, null, null, AuditResults.Rejected, null, user.Id));
                return Results.Json(new { message = "That code is not right." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            throttle.RecordSuccess(key);
            if (usedRecoveryCode)
            {
                await audit.AppendAndSaveAsync(new NewAuditEntry(
                    null, AuditActionTypes.MfaRecoveryUsed, null, null,
                    null, null, null, null, "Success", null, user.Id));
            }
            else
            {
                await db.SaveChangesAsync();
            }

            log.LogInformation("Login succeeded for user {UserId} with two-step sign-in", user.Id);
            var (token, expiresAt) = tokens.Issue(user);
            return Results.Ok(new LoginResponse(token, expiresAt, user.Username, user.Role));
        });

        // Self-registration. Mapped only when Auth:Registration:Enabled is true (on in
        // Development, off elsewhere unless configured), so otherwise the route gives 404.
        if (app.Services.GetRequiredService<AuthOptions>().Registration.Enabled)
            MapRegisterEndpoint(app);

        return app;
    }

    // No kind sent (API callers) means no door check; the role in the token still limits what the caller can do.
    // Admins sign in at the Individual door.
    private static bool RoleMatchesKind(string role, string? kind) => kind is null || kind.ToLowerInvariant() switch
    {
        "individual" => role is "user" or "admin",
        "merchant" => role is "merchant",
        "bank" => role is "bank",
        _ => false
    };

    // Anonymous by design: it is how a visitor gets an account. The role is "user" or "merchant",
    // chosen by the account kind; the request has no role field and the service has no role parameter. No token is
    // returned: the person signs in separately. Logs carry the new user's Id only: never the
    // password, hash, IP address or attempted username.
    private static void MapRegisterEndpoint(WebApplication app)
    {
        // Sign-up: only mapped when Auth:Registration:Enabled is true.
        app.MapPost("/api/auth/register", async (RegisterRequest request, HttpContext context,
            RegistrationRateLimiter limiter, UserRegistrationService registration, ILoggerFactory loggerFactory) =>
        {
            var log = loggerFactory.CreateLogger("Paysys.Api.Auth");

            // First, before any validation, hashing or database work, so every request that
            // reaches this handler costs one token, including invalid ones.
            if (!limiter.TryAcquire(context, out var retryAfter))
            {
                log.LogWarning("Registration refused: rate limit");
                var minutes = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
                return new RetryAfterResult(retryAfter,
                    $"Too many sign-up attempts from this address. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.");
            }

            var result = await registration.RegisterAsync(request.Username, request.Password, request.AccountKind);

            switch (result)
            {
                case RegistrationResult.Created created:
                    log.LogInformation("Registration created user {UserId}", created.Id);
                    return Results.Json(new RegisterResponse(created.Username), statusCode: StatusCodes.Status201Created);

                case RegistrationResult.Invalid invalid:
                    return Results.Json(new { message = invalid.Message }, statusCode: StatusCodes.Status400BadRequest);

                case RegistrationResult.UsernameTaken:
                    log.LogInformation("Registration refused: username taken");
                    return Results.Json(new { message = "That username is taken." }, statusCode: StatusCodes.Status409Conflict);

                default:
                    // Unreachable: RegistrationResult has exactly the three cases above.
                    throw new InvalidOperationException($"Unexpected registration result {result.GetType().Name}.");
            }
        })
        // 8 KB is far more than a 64-character username and a 128-character password need.
        // Enforced by routing on Kestrel; the 128-character check still stops hashing if not.
        .WithMetadata(new RequestSizeLimitAttribute(8 * 1024));
    }
}
