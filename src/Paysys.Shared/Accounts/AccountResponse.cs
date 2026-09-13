namespace Paysys.Shared.Accounts;

public record AccountResponse(
    Guid Id,
    string Name,
    decimal Balance,
    string Currency,
    string AccountType,
    int BankId,
    string BankName,
    string ClientType,
    string? TerminalId,
    DateTimeOffset CreatedAt);
