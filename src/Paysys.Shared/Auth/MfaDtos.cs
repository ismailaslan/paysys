namespace Paysys.Shared.Auth;

// Sign-in with two-step sign-in on: the password was right, now a code is needed. The challenge is a short-lived
// signed value that ties the second step to the first.
public record MfaRequiredResponse(bool MfaRequired, string Challenge);

// The code is a 6-digit authenticator code or one of the person's recovery codes.
public record MfaLoginRequest(string Challenge, string Code);
