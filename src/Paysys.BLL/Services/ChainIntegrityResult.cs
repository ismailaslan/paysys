namespace Paysys.BLL.Services;

// The newest entry of a verified chain: its id, its hash and how many entries the chain had.
// A later check that is given this head can tell when entries were removed from the end.
public record ChainHead(Guid EntryId, string Hash, int Count);

// Head is set whenever the chain walked cleanly (and also on a failed anchor check), so the caller
// can keep it as the anchor for the next check.
public record ChainIntegrityResult(bool IsValid, Guid? FirstInvalidEntryId, string? Reason, ChainHead? Head = null);
