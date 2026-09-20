using Paysys.Api.Auth;
using Paysys.Shared.Auth;

namespace Paysys.Api.Endpoints;

public static class AuthEndpoints
{
    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        // Deliberately anonymous - it is how a caller gets a token in the first place.
        app.MapPost("/api/auth/login", (LoginRequest request, UserAuthenticator authenticator, TokenService tokens,
            LoginThrottle throttle, ILoggerFactory loggerFactory) =>
        {
            var log = loggerFactory.CreateLogger("Paysys.Api.Auth");
            var key = LoginThrottle.Normalize(request.Username);

            // Checked before the password is hashed, so a locked username also costs no CPU.
            // Even a correct password is refused while locked.
            var locked = throttle.GetLockRemaining(key);
            if (locked is not null)
            {
                log.LogWarning("Login refused for {Username}: locked for another {Seconds} s", key, (int)locked.Value.TotalSeconds);
                return new RetryAfterResult(locked.Value,
                    $"Too many failed sign-in attempts. Try again in {Math.Max(1, (int)Math.Ceiling(locked.Value.TotalSeconds))} seconds.");
            }

            var user = authenticator.Authenticate(request.Username, request.Password);
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

        return app;
    }
}
