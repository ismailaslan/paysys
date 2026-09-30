using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

namespace Paysys.Api.Auth;

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddPaysysAuth(
        this IServiceCollection services, IConfiguration configuration, bool isDevelopment = false)
    {
        var jwt = configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
        var auth = configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();

        // Development-only convenience: a missing PasswordHash would otherwise fail
        // startup (see Validate below), which is right for every real environment but
        // means every fresh clone needs a user-secrets round trip before it can sign
        // in at all. In Development only, and only when the secret truly isn't set, a
        // fixed dev password is hashed in with the app's own hasher - not stored, not
        // logged, recomputed on every restart. Any PasswordHash actually present in
        // user-secrets always wins over this fallback, in every environment.
        if (isDevelopment)
        {
            var devHasher = new PasswordHasher<DemoUser>();
            foreach (var user in auth.Users.Where(u => string.IsNullOrEmpty(u.PasswordHash)))
                user.PasswordHash = devHasher.HashPassword(user, "password");
        }

        Validate(jwt, auth);

        services.AddSingleton(jwt);
        services.AddSingleton(auth);
        services.AddSingleton<UserAuthenticator>();
        // Singleton: failure counts must outlive individual requests.
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<TokenService>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Keep claim names exactly as issued ("sub", "role") instead of
                // remapping them to the long System.Security.Claims URIs.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = TokenService.CreateKey(jwt),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "name",
                    RoleClaimType = "role",
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.Admin, policy => policy.RequireRole(AuthRoles.Admin));
        });

        return services;
    }

    // Fails at startup rather than running with a missing key or an
    // unusable user - the app must not come up "open" by misconfiguration.
    private static void Validate(JwtOptions jwt, AuthOptions auth)
    {
        if (string.IsNullOrWhiteSpace(jwt.Issuer) || string.IsNullOrWhiteSpace(jwt.Audience))
            throw new InvalidOperationException("Jwt:Issuer and Jwt:Audience must be configured.");

        if (jwt.LifetimeMinutes <= 0)
            throw new InvalidOperationException("Jwt:LifetimeMinutes must be positive.");

        if (string.IsNullOrEmpty(jwt.SigningKey) || jwt.SigningKey.Length < 32)
            throw new InvalidOperationException(
                "Jwt:SigningKey is missing or shorter than 32 characters. Set it via 'dotnet user-secrets set Jwt:SigningKey \"<value>\" --project src/Paysys.Api'.");

        if (auth.Users.Count == 0)
            throw new InvalidOperationException("Auth:Users must contain at least one user.");

        foreach (var user in auth.Users)
        {
            if (user.Id == Guid.Empty || string.IsNullOrWhiteSpace(user.Username))
                throw new InvalidOperationException("Every Auth:Users entry needs an Id and a Username.");

            if (user.Role is not (AuthRoles.Admin or AuthRoles.User))
                throw new InvalidOperationException($"User '{user.Username}' has invalid Role '{user.Role}'. Use 'admin' or 'user'.");

            if (string.IsNullOrEmpty(user.PasswordHash))
                throw new InvalidOperationException(
                    $"User '{user.Username}' has no PasswordHash. Set Auth:Users:<index>:PasswordHash in user-secrets.");
        }

        if (auth.Users.Select(u => u.Username.ToLowerInvariant()).Distinct().Count() != auth.Users.Count)
            throw new InvalidOperationException("Auth:Users contains duplicate usernames.");

        if (auth.Users.Select(u => u.Id).Distinct().Count() != auth.Users.Count)
            throw new InvalidOperationException("Auth:Users contains duplicate Ids.");
    }
}
