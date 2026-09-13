namespace Paysys.Shared.Banks;

public record CreateBankRequest(string Name, string BankCode, string? ApiEndpoint);
