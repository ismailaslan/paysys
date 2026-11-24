using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Paysys.DAL.Entities;

namespace Paysys.Api.Auth;

public class TokenService
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _credentials;

    public TokenService(JwtOptions options)
    {
        _options = options;
        _credentials = new SigningCredentials(CreateKey(options), SecurityAlgorithms.HmacSha256);
    }

    public const string PasswordStampClaim = "pws";

    // A short fingerprint of the stored password hash. It is not the hash and cannot be turned back into it
    // (SHA-256, cut to 16 bytes), so putting it in a token does not leak anything useful.
    // tokenVersion is raised by "sign out everywhere"; version 0 gives the same stamp as before the counter existed,
    // so tokens issued earlier stay valid until a person uses the button.
    public static string PasswordStamp(string passwordHash, int tokenVersion = 0) =>
        Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(tokenVersion == 0 ? passwordHash : passwordHash + ":v" + tokenVersion)), 0, 16);

    public static SymmetricSecurityKey CreateKey(JwtOptions options) =>
        new(Encoding.UTF8.GetBytes(options.SigningKey));

    // The claim that marks a token as good for the two-step setup only (see the middleware in Program.cs).
    public const string SetupOnlyClaim = "mfa_setup";

    // setupOnly: a token for someone who must set up two-step sign-in first. It lasts at most 15 minutes and the API
    // refuses everything but the profile and two-step setup endpoints while it is used.
    public (string Token, DateTimeOffset ExpiresAt) Issue(User user, bool setupOnly = false)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(setupOnly ? Math.Min(15, _options.LifetimeMinutes) : _options.LifetimeMinutes);

        var claims = new List<Claim>
        {
            new("sub", user.Id.ToString()),
            new("name", user.Username),
            new("role", user.Role),
            // Ties the token to the password it was issued under. Changing the password changes the stamp,
            // so every token issued before the change stops working (see AddPaysysAuth, OnTokenValidated).
            new(PasswordStampClaim, PasswordStamp(user.PasswordHash, user.TokenVersion)),
        };
        if (setupOnly)
            claims.Add(new Claim(SetupOnlyClaim, "1"));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = _credentials,
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expiresAt);
    }
}
