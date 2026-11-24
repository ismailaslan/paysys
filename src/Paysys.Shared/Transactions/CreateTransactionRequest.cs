namespace Paysys.Shared.Transactions;

// The source is either a card token (the original way) or the id of one of the caller's own
// cards, which the server resolves to its token so the token never has to leave the server.
public record CreateTransactionRequest(
    string? SourceCardToken,
    Guid DestinationAccountId,
    decimal Amount,
    string IdempotencyKey,
    Guid? SourceCardId = null,
    // Only reserve the amount on the payer's account; the payee captures what it needs later.
    bool Hold = false);
