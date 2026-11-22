namespace Paysys.Shared.Accounts;

// A merchant adds a terminal: one more account of its own, with its own terminal id.
public record CreateTerminalRequest(string? Name);

public record TerminalResponse(Guid AccountId, string Name, string TerminalId);
