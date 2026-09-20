namespace Paysys.DAL.Entities;

public class Account
{
    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public decimal Balance { get; private set; }
    public string Currency { get; private set; }
    public AccountType AccountType { get; private set; }
    public int BankId { get; private set; }
    public ClientType ClientType { get; private set; }
    public string? TerminalId { get; private set; }
    public Guid OwnerId { get; private set; }

    // Opt-in: null means the account cannot be found by anyone but its owner. When the owner
    // creates a code, other users can look the account up by it (and learn only its name,
    // currency and bank). Unique across accounts; see PayeeCodes for the format.
    public string? PayeeCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Account()
    {
        // Required by EF Core for materialization.
        Name = string.Empty;
        Currency = string.Empty;
    }

    public Account(
        Guid id,
        string name,
        decimal balance,
        string currency,
        AccountType accountType,
        int bankId,
        ClientType clientType,
        Guid ownerId,
        string? terminalId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        if (ownerId == Guid.Empty)
            throw new ArgumentException("OwnerId is required.", nameof(ownerId));

        if (balance < 0)
            throw new ArgumentOutOfRangeException(nameof(balance), "Balance cannot be negative.");

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a 3-letter ISO code.", nameof(currency));

        if (bankId <= 0)
            throw new ArgumentOutOfRangeException(nameof(bankId), "BankId must be a positive, real Bank Id.");

        if (clientType == ClientType.Business && string.IsNullOrWhiteSpace(terminalId))
            throw new ArgumentException("TerminalId is required for Business accounts.", nameof(terminalId));

        if (clientType == ClientType.Individual && terminalId is not null)
            throw new ArgumentException("TerminalId must not be set for Individual accounts.", nameof(terminalId));

        Id = id;
        Name = name;
        Balance = balance;
        Currency = currency.ToUpperInvariant();
        AccountType = accountType;
        BankId = bankId;
        ClientType = clientType;
        OwnerId = ownerId;
        TerminalId = terminalId;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    // Null clears the code (the account stops being discoverable).
    public void SetPayeeCode(string? payeeCode)
    {
        if (payeeCode is not null && (payeeCode.Length > 20 || string.IsNullOrWhiteSpace(payeeCode)))
            throw new ArgumentException("PayeeCode must be 1-20 characters.", nameof(payeeCode));

        PayeeCode = payeeCode;
    }

    public void Debit(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");

        if (amount > Balance)
            throw new InvalidOperationException("Insufficient funds.");

        Balance -= amount;
    }

    public void Credit(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");

        Balance += amount;
    }
}
