namespace Paysys.Shared.Profile;

// What a signed-in person sees about themselves. Email and phone are optional and not verified.
public record ProfileResponse(string Username, string Role, DateTimeOffset CreatedAt, bool HasPin, string? Email, string? Phone, bool MfaEnabled = false);

// Both fields replace what is stored; an empty or missing value clears it.
public record UpdateContactRequest(string? Email, string? Phone);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

// Erase my account: the password is asked again so a borrowed session cannot do it.
public record DeleteAccountRequest(string Password);
