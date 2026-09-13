namespace Paysys.Shared.BankRouting;

public record BankApprovalResponse(
    bool Approved,
    string? Reason);
