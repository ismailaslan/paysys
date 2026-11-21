namespace Paysys.Api.Auth;

// The one place the password length limits are defined. Registration
// (UserRegistrationService) enforces both; login (UserAuthenticator) refuses anything over
// MaxLength before any database lookup or hashing.
//
// Lengths are string.Length, i.e. UTF-16 code units, not Unicode characters. There are no
// composition rules, no trimming and no Unicode normalization (minimum is 6 by the owner's
// decision: demo project, no real users).
public static class PasswordRules
{
    public const int MinLength = 6;
    public const int MaxLength = 128;
}
