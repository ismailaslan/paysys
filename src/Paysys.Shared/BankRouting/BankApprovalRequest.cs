namespace Paysys.Shared.BankRouting;

public record BankApprovalRequest(
    decimal Amount,
    string FromAccountReference,
    string ToAccountReference);
