namespace Paysys.Shared.Banks;

public record BankCustomerAccount(
    string AccountName,
    string OwnerUsername,
    string ClientType,
    string Currency,
    decimal Balance,
    string? AccountNumber);

public record BankCustomerCard(
    Guid CardId,
    string LastFourDigits,
    string CardBrand,
    string AccountName,
    string OwnerUsername,
    string Status);

public record BankTransactionRow(
    DateTimeOffset CreatedAt,
    decimal Amount,
    string Currency,
    string Status,
    string FromAccount,
    string ToAccount);

// What a bank sees of itself: its registration, the accounts customers hold with it, and the
// latest payments that touched them.
public record BankOverviewResponse(
    string BankName,
    string BankCode,
    string? ApiEndpoint,
    BankCustomerAccount[] Customers,
    BankTransactionRow[] Transactions,
    BankCustomerCard[]? Cards = null);
