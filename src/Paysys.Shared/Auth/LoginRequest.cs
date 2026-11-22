namespace Paysys.Shared.Auth;

// AccountKind is the sign-in door the person chose ("individual", "merchant" or "bank"). When it is
// sent, the account's role must belong to that kind, otherwise the sign-in is refused like a wrong password.
public record LoginRequest(string Username, string Password, string? AccountKind = null);
