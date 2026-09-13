namespace Paysys.Shared.Banks;

public record BankResponse(int Id, string Name, string BankCode, string? ApiEndpoint);
