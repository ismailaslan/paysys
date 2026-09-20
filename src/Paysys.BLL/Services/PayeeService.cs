using Microsoft.EntityFrameworkCore;
using Paysys.BLL.Exceptions;
using Paysys.DAL.Persistence;

namespace Paysys.BLL.Services;

// What a lookup reveals about an account: enough to recognize who you are about to pay,
// and nothing else (no balance, owner, account type or terminal).
public sealed record PayeeInfo(Guid AccountId, string DisplayName, string Currency, string BankName);

// Opt-in payee discovery. An account can only be found by another user after its owner
// creates a payee code; the owner can regenerate or remove it at any time.
//
// Only the owner may manage a code, with no admin bypass - the code decides who can find the
// account, which is the owner's call. Changes to a code are audited in the same commit that
// makes them.
public class PayeeService
{
    private const int MaxGenerateAttempts = 5;

    private readonly PaysysDbContext _db;
    private readonly TransactionAuditLogService _audit;

    public PayeeService(PaysysDbContext db, TransactionAuditLogService audit)
    {
        _db = db;
        _audit = audit;
    }

    // Creates a new code for the account, replacing (and so revoking) any previous one.
    public async Task<string> GenerateCodeAsync(Guid requestingUserId, Guid accountId)
    {
        for (var attempt = 0; attempt < MaxGenerateAttempts; attempt++)
        {
            var account = await LoadOwnedAccountAsync(requestingUserId, accountId);
            var code = PayeeCodes.Generate();
            account.SetPayeeCode(code);

            try
            {
                // The code change and its audit entry commit together.
                await _audit.AppendAndSaveAsync(new NewAuditEntry(
                    null, AuditActionTypes.PayeeCodeGenerated, null, null,
                    accountId.ToString(), null, null, null, "Success", null, requestingUserId));

                return code;
            }
            catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, UniqueViolation.AccountPayeeCodeIndex))
            {
                // Vanishingly unlikely (50 bits), but it is a unique index: drop the pending
                // change and try another code.
                _db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not generate a unique payee code.");
    }

    // Returns false if the account had no code (nothing to do).
    public async Task<bool> RevokeCodeAsync(Guid requestingUserId, Guid accountId)
    {
        var account = await LoadOwnedAccountAsync(requestingUserId, accountId);
        if (account.PayeeCode is null)
            return false;

        account.SetPayeeCode(null);

        await _audit.AppendAndSaveAsync(new NewAuditEntry(
            null, AuditActionTypes.PayeeCodeRevoked, null, null,
            accountId.ToString(), null, null, null, "Success", null, requestingUserId));

        return true;
    }

    // Finds the account a (normalized) code belongs to, or null. An unknown code is recorded
    // as a rejected attempt and draws on the caller's guess budget - which is the only thing
    // making the code space unenumerable - so over budget this throws
    // AccessDenialRateLimitedException instead of answering.
    public async Task<PayeeInfo?> LookupAsync(Guid requestingUserId, string normalizedCode)
    {
        var account = await _db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.PayeeCode == normalizedCode);

        if (account is null)
        {
            await _audit.RecordRejectedAttemptAsync(
                requestingUserId, AuditActionTypes.RejectedPayeeLookup,
                reason: $"code {PayeeCodes.Fingerprint(normalizedCode)}");
            return null;
        }

        var bankName = await _db.Banks.Where(b => b.Id == account.BankId).Select(b => b.Name).SingleAsync();
        return new PayeeInfo(account.Id, account.Name, account.Currency, bankName);
    }

    private async Task<DAL.Entities.Account> LoadOwnedAccountAsync(Guid requestingUserId, Guid accountId)
    {
        var account = await _db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId)
            ?? throw new AccountNotFoundException(accountId);

        if (account.OwnerId != requestingUserId)
        {
            await _audit.RecordAccessDeniedAsync(requestingUserId, AuditActionTypes.AccessDeniedPayeeCode, accountId);
            throw new AccountAccessDeniedException(accountId);
        }

        return account;
    }
}
