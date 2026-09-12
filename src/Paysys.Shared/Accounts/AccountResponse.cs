namespace Paysys.Shared.Accounts;

public record AccountResponse(Guid Id, string Name, decimal Balance, string Currency, string AccountType, DateTimeOffset CreatedAt);
