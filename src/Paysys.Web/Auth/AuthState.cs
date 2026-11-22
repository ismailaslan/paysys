using System.Text.Json;
using Microsoft.JSInterop;
using Paysys.Shared.Auth;

namespace Paysys.Web.Auth;

// Holds the signed-in user's bearer token. It is also kept in sessionStorage so a page
// reload does not sign the user out. sessionStorage is cleared when the tab is closed and is
// never shared with other tabs, but it is readable by any script on this page, so a
// cross-site-scripting bug could steal the token. That trade-off was accepted: this is a
// demo with a 30-minute token. An expired token is discarded on load, never used.
public class AuthState
{
    private const string StorageKey = "paysys.session";
    private const string ThemeKey = "paysys.theme";

    private readonly IJSInProcessRuntime? _storage;
    private string? _token;
    private DateTimeOffset _expiresAt;

    public AuthState(IJSRuntime js)
    {
        // In Blazor WebAssembly the runtime is in-process, so storage can be read synchronously.
        _storage = js as IJSInProcessRuntime;
        Restore();
    }

    public string? Username { get; private set; }
    public string? Role { get; private set; }

    // The sign-in tab the user chose (individual, merchant or bank). It only sets the page colour.
    public string Theme { get; private set; } = "individual";

    public event Action? Changed;

    public bool IsAuthenticated => _token is not null && DateTimeOffset.UtcNow < _expiresAt;
    public bool IsAdmin => IsAuthenticated && Role == "admin";
    public bool IsMerchant => IsAuthenticated && Role == "merchant";
    public bool IsBank => IsAuthenticated && Role == "bank";

    // Null once the token has expired, so an expired token is never attached.
    public string? Token => IsAuthenticated ? _token : null;

    public void SignIn(LoginResponse response, string theme = "individual")
    {
        Apply(response);
        Theme = NormalizeTheme(theme);
        try
        {
            _storage?.InvokeVoid("sessionStorage.setItem", StorageKey, JsonSerializer.Serialize(response));
            _storage?.InvokeVoid("sessionStorage.setItem", ThemeKey, Theme);
        }
        catch (JSException)
        {
            // Storage unavailable (for example blocked by the browser): the session lasts until reload.
        }

        Changed?.Invoke();
    }

    public void SignOut()
    {
        _token = null;
        _expiresAt = default;
        Username = null;
        Role = null;
        Theme = "individual";
        Forget();
        Changed?.Invoke();
    }

    private void Restore()
    {
        try
        {
            var json = _storage?.Invoke<string?>("sessionStorage.getItem", StorageKey);
            if (string.IsNullOrEmpty(json))
                return;

            var saved = JsonSerializer.Deserialize<LoginResponse>(json);
            if (saved is null || saved.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                Forget();
                return;
            }

            Apply(saved);
            Theme = NormalizeTheme(_storage?.Invoke<string?>("sessionStorage.getItem", ThemeKey));
        }
        catch (Exception ex) when (ex is JSException or JsonException)
        {
            Forget();
        }
    }

    private void Apply(LoginResponse response)
    {
        _token = response.Token;
        _expiresAt = response.ExpiresAt;
        Username = response.Username;
        Role = response.Role;
    }

    private static string NormalizeTheme(string? theme) => theme is "merchant" or "bank" ? theme : "individual";

    private void Forget()
    {
        try
        {
            _storage?.InvokeVoid("sessionStorage.removeItem", StorageKey);
            _storage?.InvokeVoid("sessionStorage.removeItem", ThemeKey);
        }
        catch (JSException)
        {
        }
    }
}
