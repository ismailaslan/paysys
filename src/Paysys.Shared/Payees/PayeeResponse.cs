namespace Paysys.Shared.Payees;

// What a payee-code lookup reveals: enough to recognize who you are about to pay, nothing more.
public record PayeeResponse(
    Guid AccountId,
    string DisplayName,
    string Currency,
    string BankName);
