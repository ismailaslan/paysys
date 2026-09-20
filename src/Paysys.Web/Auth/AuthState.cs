using Paysys.Shared.Auth;

namespace Paysys.Web.Auth;

// Holds the signed-in user's bearer token in memory only. It is deliberately not
// written to localStorage/sessionStorage/cookies: a page reload signs the user out,
// which is the trade-off for keeping the token out of anything script-readable
// that outlives the tab.
public class AuthState
{
    private string? _token;
    private DateTimeOffset _expiresAt;

    public string? Username { get; private set; }
    public string? Role { get; private set; }

    public event Action? Changed;

    public bool IsAuthenticated => _token is not null && DateTimeOffset.UtcNow < _expiresAt;
    public bool IsAdmin => IsAuthenticated && Role == "admin";

    // Null once the token has expired, so an expired token is never attached.
    public string? Token => IsAuthenticated ? _token : null;

    public void SignIn(LoginResponse response)
    {
        _token = response.Token;
        _expiresAt = response.ExpiresAt;
        Username = response.Username;
        Role = response.Role;
        Changed?.Invoke();
    }

    public void SignOut()
    {
        _token = null;
        _expiresAt = default;
        Username = null;
        Role = null;
        Changed?.Invoke();
    }
}
