using System.Security.Claims;

namespace Paysys.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    // The user's Id from the token's "sub" claim - the value Account.OwnerId is
    // compared against. Null only if the token has no usable sub, which a token
    // issued by TokenService never lacks.
    public static Guid? GetUserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst("sub")?.Value, out var id) ? id : null;

    // Admin widens what a caller may READ. It is never consulted for writes:
    // tokenizing or spending from an account stays owner-only for everyone.
    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(AuthRoles.Admin);
}
