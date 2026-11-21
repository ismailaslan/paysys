namespace Paysys.Shared.Payees;

// A short-lived, single-use code for receiving a payment (see PaymentCodeStore).
public record PaymentCodeResponse(string Code, DateTimeOffset ExpiresAt);
