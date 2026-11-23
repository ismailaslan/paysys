using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.Shared.Accounts;
using Paysys.Shared.Tokenization;

namespace Paysys.Api.Endpoints;

// Card status and terminals.
// - The owner can pause, resume and delete their own card. Someone else's card id gets the same 404
//   as one that does not exist.
// - A bank can block and unblock cards on accounts held at that bank. Only the bank can undo a block.
// - A merchant can add terminals (more accounts of its own, each with a terminal id), up to a limit.
// Every change is written to the audit chain together with the change itself, and never carries the
// card number or token.
public static class CardEndpoints
{
    public const int MaxTerminalsPerMerchant = 5;
    private const int MaxTerminalNameLength = 60;

    public static WebApplication MapCardEndpoints(this WebApplication app)
    {
        app.MapPost("/api/cards/{cardId:guid}/pause", (Guid cardId, ClaimsPrincipal user, PaysysDbContext db, TransactionAuditLogService audit) =>
            ChangeAsOwnerAsync(cardId, user, db, audit, AuditActionTypes.CardPaused, card =>
            {
                if (card.Status != CardStatus.Active)
                    return card.Status == CardStatus.Blocked ? "Your bank has blocked this card." : "Only an active card can be paused.";
                card.Pause();
                return null;
            })).RequireAuthorization();

        app.MapPost("/api/cards/{cardId:guid}/resume", (Guid cardId, ClaimsPrincipal user, PaysysDbContext db, TransactionAuditLogService audit) =>
            ChangeAsOwnerAsync(cardId, user, db, audit, AuditActionTypes.CardResumed, card =>
            {
                if (card.Status == CardStatus.Blocked)
                    return "Your bank has blocked this card. Only the bank can unblock it.";
                if (card.Status != CardStatus.Paused)
                    return "Only a paused card can be resumed.";
                card.Resume();
                return null;
            })).RequireAuthorization();

        // "Delete" closes the card: it disappears from every list and can never pay again, but the row
        // stays because past payments point at it.
        app.MapDelete("/api/cards/{cardId:guid}", (Guid cardId, ClaimsPrincipal user, PaysysDbContext db, TransactionAuditLogService audit) =>
            ChangeAsOwnerAsync(cardId, user, db, audit, AuditActionTypes.CardClosed, card =>
            {
                if (card.Status == CardStatus.Blocked)
                    return "Your bank has blocked this card. Only the bank can unblock it.";
                if (card.Status == CardStatus.Closed)
                    return "This card is already deleted.";
                card.Close();
                return null;
            })).RequireAuthorization();

        app.MapPost("/api/bank/cards/{cardId:guid}/block", (Guid cardId, ClaimsPrincipal user, PaysysDbContext db, TransactionAuditLogService audit) =>
            ChangeAsBankAsync(cardId, user, db, audit, AuditActionTypes.CardBlocked, card =>
            {
                if (card.Status is not (CardStatus.Active or CardStatus.Paused))
                    return "Only an active or paused card can be blocked.";
                card.Block();
                return null;
            })).RequireAuthorization(policy => policy.RequireRole(AuthRoles.Bank));

        app.MapPost("/api/bank/cards/{cardId:guid}/unblock", (Guid cardId, ClaimsPrincipal user, PaysysDbContext db, TransactionAuditLogService audit) =>
            ChangeAsBankAsync(cardId, user, db, audit, AuditActionTypes.CardUnblocked, card =>
            {
                if (card.Status != CardStatus.Blocked)
                    return "Only a blocked card can be unblocked.";
                card.Unblock();
                return null;
            })).RequireAuthorization(policy => policy.RequireRole(AuthRoles.Bank));

        app.MapPost("/api/merchant/terminals", async (CreateTerminalRequest request, ClaimsPrincipal user, PaysysDbContext db, TransactionAuditLogService audit) =>
        {
            var userId = user.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var tills = await db.Accounts.AsNoTracking()
                .Where(a => a.OwnerId == userId.Value && a.ClientType == ClientType.Business)
                .ToListAsync();
            if (tills.Count == 0)
                return Results.NotFound(new { message = "This login has no merchant account." });

            if (tills.Count >= MaxTerminalsPerMerchant)
                return Results.Conflict(new { message = $"A merchant can have {MaxTerminalsPerMerchant} terminals, which is the demo limit." });

            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                var username = await db.Users.AsNoTracking().Where(u => u.Id == userId.Value).Select(u => u.Username).FirstAsync();
                name = $"{username} Terminal {tills.Count + 1}";
            }

            if (name.Length > MaxTerminalNameLength)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [$"The name must be at most {MaxTerminalNameLength} characters."] });

            var first = tills[0];

            // The generated account number can in theory clash (about 1 in 2^50): try again with a new one.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var terminalId = UserRegistrationService.NewTerminalId();
                var account = new Account(
                    Guid.NewGuid(), name, 0m, first.Currency, AccountType.Person, first.BankId,
                    ClientType.Business, userId.Value, terminalId);
                account.SetPayeeCode(PayeeCodes.Generate());
                db.Accounts.Add(account);

                try
                {
                    await audit.AppendAndSaveAsync(new NewAuditEntry(
                        null, AuditActionTypes.TerminalAdded, null, null, account.Id.ToString(), null, null,
                        terminalId, "Success", null, userId.Value));
                    return Results.Created((string?)null, new TerminalResponse(account.Id, account.Name, terminalId));
                }
                catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, UniqueViolation.AccountPayeeCodeIndex))
                {
                    db.ChangeTracker.Clear();
                }
            }

            throw new InvalidOperationException("Could not generate a unique account number.");
        }).RequireAuthorization(policy => policy.RequireRole(AuthRoles.Merchant));

        return app;
    }

    private static async Task<IResult> ChangeAsOwnerAsync(
        Guid cardId, ClaimsPrincipal user, PaysysDbContext db, TransactionAuditLogService audit,
        string actionType, Func<CardToken, string?> apply)
    {
        var userId = user.GetUserId();
        if (userId is null)
            return Results.Unauthorized();

        var card = await db.CardTokens.SingleOrDefaultAsync(c => c.Id == cardId
            && db.Accounts.Any(a => a.Id == c.AccountId && a.OwnerId == userId.Value));
        if (card is null)
            return Results.NotFound(new { message = "Card not found." });

        return await ApplyAsync(card, userId.Value, db, audit, actionType, apply);
    }

    private static async Task<IResult> ChangeAsBankAsync(
        Guid cardId, ClaimsPrincipal user, PaysysDbContext db, TransactionAuditLogService audit,
        string actionType, Func<CardToken, string?> apply)
    {
        var userId = user.GetUserId();
        if (userId is null)
            return Results.Unauthorized();

        var bankId = await db.Accounts.AsNoTracking()
            .Where(a => a.OwnerId == userId.Value && a.AccountType == AccountType.Bank)
            .Select(a => (int?)a.BankId)
            .FirstOrDefaultAsync();
        if (bankId is null)
            return Results.NotFound(new { message = "No bank is linked to this login." });

        // Only cards on accounts held at this bank; another bank's card is a plain 404.
        var card = await db.CardTokens.SingleOrDefaultAsync(c => c.Id == cardId
            && db.Accounts.Any(a => a.Id == c.AccountId && a.BankId == bankId.Value));
        if (card is null)
            return Results.NotFound(new { message = "Card not found." });

        return await ApplyAsync(card, userId.Value, db, audit, actionType, apply);
    }

    private static async Task<IResult> ApplyAsync(
        CardToken card, Guid actorUserId, PaysysDbContext db, TransactionAuditLogService audit,
        string actionType, Func<CardToken, string?> apply)
    {
        var original = card.Status;
        var problem = apply(card);
        if (problem is not null)
            return Results.Conflict(new { message = problem });

        if (!await CardStatusChange.SaveAsync(db, audit, card, original, actionType, actorUserId))
            return Results.Conflict(new { message = "The card was changed by someone else. Reload and try again." });

        return Results.Ok(new CardStatusResponse(card.Id, card.Status.ToString()));
    }
}

// Saves a card's new status only if the card still has the status the change was based on, in the same
// database transaction as its audit entry. Without this, an owner's Resume and a bank's Block that run at
// the same time could overwrite each other and the last one saved would win (security review finding 4).
public static class CardStatusChange
{
    private sealed class CardChangedException : Exception;

    // False when the card's status changed in the meantime; nothing is saved then.
    public static async Task<bool> SaveAsync(
        PaysysDbContext db, TransactionAuditLogService audit, CardToken card, CardStatus original,
        string actionType, Guid actorUserId)
    {
        var newStatus = card.Status;

        // The status is written by the conditional update below, not by the change tracker.
        db.Entry(card).Property(c => c.Status).IsModified = false;

        try
        {
            await audit.AppendAndSaveAsync(
                new NewAuditEntry(
                    null, actionType, null, null, card.AccountId.ToString(), null, null, null, "Success", null, actorUserId),
                async () =>
                {
                    var changed = await db.CardTokens
                        .Where(c => c.Id == card.Id && c.Status == original)
                        .ExecuteUpdateAsync(set => set.SetProperty(c => c.Status, newStatus));
                    if (changed == 0)
                        throw new CardChangedException();
                });
            return true;
        }
        catch (CardChangedException)
        {
            return false;
        }
    }
}
