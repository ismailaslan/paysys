namespace Paysys.Shared.Auth;

// Self-registration body. There is deliberately no Role property: every registered account
// gets role "user" on the server, and a "role" field in the JSON is ignored.
public record RegisterRequest(string Username, string Password);
