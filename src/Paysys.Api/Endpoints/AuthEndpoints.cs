using Microsoft.AspNetCore.Mvc;
using Paysys.Api.Auth;
using Paysys.Shared.Auth;

namespace Paysys.Api.Endpoints;

public static class AuthEndpoints
{
    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        // Deliberately anonymous - it is how a caller gets a token in the first place.
        app.MapPost("/api/auth/login", async (LoginRequest request, UserAuthenticator authenticator, TokenService tokens,
            LoginThrottle throttle, ILoggerFactory loggerFactory) =>
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

            throttle.RecordSuccess(key);
            log.LogInformation("Login succeeded for user {UserId}", user.Id);

            var (token, expiresAt) = tokens.Issue(user);
            return Results.Ok(new LoginResponse(token, expiresAt, user.Username, user.Role));
        });

        // Self-registration. Mapped only when Auth:Registration:Enabled is true (on in
        // Development, off elsewhere unless configured), so otherwise the route gives 404.
        if (app.Services.GetRequiredService<AuthOptions>().Registration.Enabled)
            MapRegisterEndpoint(app);

        return app;
    }

    // Anonymous by design: it is how a visitor gets an account. Every account is role "user";
    // the request has no role field and the service has no role parameter. No token is
    // returned: the person signs in separately. Logs carry the new user's Id only: never the
    // password, hash, IP address or attempted username.
    private static void MapRegisterEndpoint(WebApplication app)
    {
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

            var result = await registration.RegisterAsync(request.Username, request.Password);

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
