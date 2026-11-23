namespace Paysys.Shared.Payees;

// What a payee-code lookup reveals: enough to recognize who you are about to pay, nothing more.
public record PayeeResponse(
    Guid AccountId,
    string DisplayName,
    string Currency,
    string BankName,
    // Set when the payment code carried an amount: the only amount this payment may have.
    decimal? RequiredAmount = null);
