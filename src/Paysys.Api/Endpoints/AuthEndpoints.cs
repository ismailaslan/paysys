using Paysys.Api.Auth;
using Paysys.Shared.Auth;

namespace Paysys.Api.Endpoints;

public static class AuthEndpoints
{
    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        // Deliberately anonymous - it is how a caller gets a token in the first place.
        app.MapPost("/api/auth/login", (LoginRequest request, UserAuthenticator authenticator, TokenService tokens) =>
        {
            var user = authenticator.Authenticate(request.Username, request.Password);
            if (user is null)
            {
                // Same message for "no such user" and "wrong password".
                return Results.Json(
                    new { message = "Invalid username or password." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var (token, expiresAt) = tokens.Issue(user);
            return Results.Ok(new LoginResponse(token, expiresAt, user.Username, user.Role));
        });

        return app;
    }
}
