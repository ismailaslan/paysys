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
    public static string PasswordStamp(string passwordHash) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash)), 0, 16);

    public static SymmetricSecurityKey CreateKey(JwtOptions options) =>
        new(Encoding.UTF8.GetBytes(options.SigningKey));

    public (string Token, DateTimeOffset ExpiresAt) Issue(User user)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(_options.LifetimeMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", user.Id.ToString()),
                new Claim("name", user.Username),
                new Claim("role", user.Role),
                // Ties the token to the password it was issued under. Changing the password changes the stamp,
                // so every token issued before the change stops working (see AddPaysysAuth, OnTokenValidated).
                new Claim(PasswordStampClaim, PasswordStamp(user.PasswordHash)),
            ]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = _credentials,
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expiresAt);
    }
}
