namespace Paysys.Domain.Entities;

public class Bank
{
    public int Id { get; private set; }
    public string Name { get; private set; }
    public string BankCode { get; private set; }
    public string? ApiEndpoint { get; private set; }

    private Bank()
    {
        // Required by EF Core for materialization.
        Name = string.Empty;
        BankCode = string.Empty;
    }

    public Bank(string name, string bankCode, string? apiEndpoint = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        if (string.IsNullOrWhiteSpace(bankCode))
            throw new ArgumentException("BankCode is required.", nameof(bankCode));

        Name = name;
        BankCode = bankCode;
        ApiEndpoint = apiEndpoint;
    }
}
