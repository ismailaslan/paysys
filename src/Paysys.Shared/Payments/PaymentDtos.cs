namespace Paysys.Shared.Payments;

// Set or change the 6-digit payment PIN. CurrentPin is required only when a PIN is already set.
public record SetPinRequest(string Pin, string? CurrentPin = null);

public record PinStatusResponse(bool IsSet);

// Step two of a payment: the PIN that confirms the pending payment.
public record ConfirmPaymentRequest(string Pin);

// Step one of a payment: the server holds the request for a few minutes until it is confirmed.
public record PendingPaymentResponse(Guid PaymentId, DateTimeOffset ExpiresAt);
