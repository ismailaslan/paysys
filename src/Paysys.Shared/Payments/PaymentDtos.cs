namespace Paysys.Shared.Payments;

// Set or change the 6-digit payment PIN. CurrentPin is required only when a PIN is already set.
public record SetPinRequest(string Pin, string? CurrentPin = null);

public record PinStatusResponse(bool IsSet);

// Step two of a payment: the PIN that confirms the pending payment.
public record ConfirmPaymentRequest(string Pin);

// Step one of a payment: the server holds the request for a few minutes until it is confirmed.
public record PendingPaymentResponse(Guid PaymentId, DateTimeOffset ExpiresAt);

// A hold: money reserved on a payer's account that the payee can capture (up to the held amount) or release.
public record HoldResponse(Guid HoldId, decimal Amount, string Currency, string Status, DateTimeOffset ExpiresAt);

// One hold in a list. Amount and Currency are what is reserved on the payer's side; DestinationAmount is the most
// the payee can capture, in its own currency. Counterparty is the other side's name.
public record HoldItemResponse(
    Guid Id, decimal Amount, string Currency, decimal DestinationAmount, string DestinationCurrency,
    string Status, string CounterpartyName, decimal? CapturedAmount, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public record CaptureHoldRequest(decimal Amount);
