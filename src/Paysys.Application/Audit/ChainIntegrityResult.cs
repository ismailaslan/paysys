namespace Paysys.Application.Audit;

public record ChainIntegrityResult(bool IsValid, Guid? FirstInvalidEntryId, string? Reason);
