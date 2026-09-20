using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

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

    public static SymmetricSecurityKey CreateKey(JwtOptions options) =>
        new(Encoding.UTF8.GetBytes(options.SigningKey));

    public (string Token, DateTimeOffset ExpiresAt) Issue(DemoUser user)
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
            ]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = _credentials,
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expiresAt);
    }
}
