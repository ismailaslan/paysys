namespace Paysys.Shared.Auth;

public record LoginResponse(
    string Token,
    DateTimeOffset ExpiresAt,
    string Username,
    string Role);
