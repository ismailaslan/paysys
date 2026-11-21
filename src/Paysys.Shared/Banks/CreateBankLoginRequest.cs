namespace Paysys.Shared.Banks;

// Admin only: creates a login for a bank. The bank is named in the URL.
public record CreateBankLoginRequest(string Username, string Password);
