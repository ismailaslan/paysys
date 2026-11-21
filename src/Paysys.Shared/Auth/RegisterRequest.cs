namespace Paysys.Shared.Auth;

// Self-registration body. There is deliberately no Role property: the server derives the role
// from AccountKind ("individual", the default, gives role "user"; "merchant" gives role
// "merchant"), and a "role" field in the JSON is ignored.
public record RegisterRequest(string Username, string Password, string? AccountKind = null);
