namespace Paysys.Shared.Auth;

// Returned with 201. No token: the person signs in separately on the Sign in page.
public record RegisterResponse(string Username);
