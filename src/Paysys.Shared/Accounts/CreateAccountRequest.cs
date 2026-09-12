namespace Paysys.Shared.Accounts;

public record CreateAccountRequest(string Name, decimal Balance, string Currency, string AccountType);
