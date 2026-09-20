namespace Paysys.BLL.Services;

public record ChainIntegrityResult(bool IsValid, Guid? FirstInvalidEntryId, string? Reason);
