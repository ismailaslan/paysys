namespace Paysys.Shared.Profile;

// Set-up: the shared secret as text to type into an authenticator app, and the same as an otpauth link.
public record MfaStartResponse(string Secret, string OtpAuthUri);

public record MfaCodeRequest(string Code);

// Shown once, when two-step sign-in is turned on: each recovery code works one time.
public record MfaEnabledResponse(string[] RecoveryCodes);

public record MfaDisableRequest(string Password, string Code);
