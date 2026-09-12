namespace Paysys.Domain.Entities;

public class Account
{
    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public decimal Balance { get; private set; }
    public string Currency { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Account()
    {
        // Required by EF Core for materialization.
        Name = string.Empty;
        Currency = string.Empty;
    }

    public Account(Guid id, string name, decimal balance, string currency)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        if (balance < 0)
            throw new ArgumentOutOfRangeException(nameof(balance), "Balance cannot be negative.");

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a 3-letter ISO code.", nameof(currency));

        Id = id;
        Name = name;
        Balance = balance;
        Currency = currency.ToUpperInvariant();
        CreatedAt = DateTimeOffset.UtcNow;
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
