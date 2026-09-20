namespace Paysys.Api.Auth;

public class JwtOptions
{
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public int LifetimeMinutes { get; set; } = 30;

    // Comes from user-secrets (Jwt:SigningKey) - never from appsettings.json.
    public string SigningKey { get; set; } = "";
}

// A user seeded from configuration: Id/Username/Role live in appsettings.json,
// PasswordHash lives in user-secrets (Auth:Users:<n>:PasswordHash). There is no
// user table - Account.OwnerId holds one of these Ids without a database FK.
public class DemoUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string Role { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

public class AuthOptions
{
    public List<DemoUser> Users { get; set; } = [];
}

public static class AuthRoles
{
    public const string Admin = "admin";
    public const string User = "user";
}

public static class AuthPolicies
{
    public const string Admin = "admin";
}
