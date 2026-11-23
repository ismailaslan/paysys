using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Auth;

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddPaysysAuth(
        this IServiceCollection services, IConfiguration configuration, bool isDevelopment = false)
    {
        var jwt = configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
        // Auth:Users is no longer what login checks (the Users table is). It is bound only so
        // the one-shot --import-users command (ConfigUserImporter) can copy it into the table,
        // and it is validated there, where the data is written. There is no development
        // password fallback: every imported user needs a real PasswordHash.
        var auth = configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();

        Validate(jwt);

        services.AddSingleton(jwt);
        services.AddSingleton(auth);
        // Scoped: it reads the Users table through the request's PaysysDbContext.
        services.AddScoped<UserAuthenticator>();
        services.AddScoped<ConfigUserImporter>();
        // Scoped: it writes the Users table through the request's PaysysDbContext.
        services.AddScoped<UserRegistrationService>();
        services.AddScoped<DemoShopService>();
        // Singleton: failure counts must outlive individual requests.
        services.AddSingleton<LoginThrottle>();
        // Singleton: the per-address sign-up buckets must outlive individual requests.
        services.AddSingleton<RegistrationRateLimiter>();
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

                // A signature and an expiry are not enough: the token must also match the user's current
                // password. After a password change the stamp no longer matches, so older tokens are refused.
                // The cost is one small read of the Users table per request.
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal;
                        var sub = principal?.FindFirst("sub")?.Value;
                        var stamp = principal?.FindFirst(TokenService.PasswordStampClaim)?.Value;
                        if (!Guid.TryParse(sub, out var userId) || string.IsNullOrEmpty(stamp))
                        {
                            context.Fail("The token has no password stamp.");
                            return;
                        }

                        var db = context.HttpContext.RequestServices.GetRequiredService<PaysysDbContext>();
                        var hash = await db.Users.AsNoTracking()
                            .Where(u => u.Id == userId)
                            .Select(u => u.PasswordHash)
                            .SingleOrDefaultAsync(context.HttpContext.RequestAborted);

                        if (hash is null ||
                            !CryptographicOperations.FixedTimeEquals(
                                Encoding.UTF8.GetBytes(TokenService.PasswordStamp(hash)),
                                Encoding.UTF8.GetBytes(stamp)))
                            context.Fail("The password has changed since this token was issued.");
                    },
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.Admin, policy => policy.RequireRole(AuthRoles.Admin));
        });

        return services;
    }

    // Fails at startup rather than running with a missing or weak signing key - the app
    // must not come up "open" by misconfiguration. Users are not checked here: with no row
    // in the Users table nobody can sign in, which fails closed.
    private static void Validate(JwtOptions jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt.Issuer) || string.IsNullOrWhiteSpace(jwt.Audience))
            throw new InvalidOperationException("Jwt:Issuer and Jwt:Audience must be configured.");

        if (jwt.LifetimeMinutes <= 0)
            throw new InvalidOperationException("Jwt:LifetimeMinutes must be positive.");

        if (string.IsNullOrEmpty(jwt.SigningKey) || jwt.SigningKey.Length < 32)
            throw new InvalidOperationException(
                "Jwt:SigningKey is missing or shorter than 32 characters. Set it via 'dotnet user-secrets set Jwt:SigningKey \"<value>\" --project src/Paysys.Api'.");
    }
}
