namespace Paysys.Shared.Platform;

// Admin only: sets a new password for the named user.
public record ResetPasswordRequest(string Username, string NewPassword);
