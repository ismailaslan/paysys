using Microsoft.EntityFrameworkCore;
using Paysys.BLL.Exceptions;
using Paysys.DAL.Persistence;

namespace Paysys.BLL.Services;

public sealed record PaymentCodeIssued(string Code, DateTimeOffset ExpiresAt);

// Creating and using short payment codes. Only the owner may create one for an account (no admin
// bypass), and both creating and using one are audited. The audit entries never contain the code.
public class PaymentCodeService
{
    private readonly PaysysDbContext _db;
    private readonly TransactionAuditLogService _audit;
    private readonly PaymentCodeStore _store;

    public PaymentCodeService(PaysysDbContext db, TransactionAuditLogService audit, PaymentCodeStore store)
    {
        _db = db;
        _audit = audit;
        _store = store;
    }

    public async Task<PaymentCodeIssued> CreateAsync(Guid requestingUserId, Guid accountId)
    {
        var account = await _db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId)
            ?? throw new AccountNotFoundException(accountId);

        if (account.OwnerId != requestingUserId)
        {
            await _audit.RecordAccessDeniedAsync(requestingUserId, AuditActionTypes.AccessDeniedPayeeCode, accountId);
            throw new AccountAccessDeniedException(accountId);
        }

        var (code, expiresAt) = _store.Create(accountId);

        await _audit.AppendAndSaveAsync(new NewAuditEntry(
            null, AuditActionTypes.PaymentCodeCreated, null, null,
            accountId.ToString(), null, null, null, "Success", null, requestingUserId));

        return new PaymentCodeIssued(code, expiresAt);
    }

    // Resolves and uses up a code. An unknown, used or expired code draws on the caller's guess
    // budget (over budget this throws AccessDenialRateLimitedException).
    public async Task<PayeeInfo?> LookupAsync(Guid requestingUserId, string code)
    {
        if (!_store.TryConsume(code, out var accountId))
        {
            await _audit.RecordRejectedAttemptAsync(
                requestingUserId, AuditActionTypes.RejectedPayeeLookup, reason: "payment code");
            return null;
        }

        var account = await _db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId);
        if (account is null)
            return null;

        var bankName = await _db.Banks.Where(b => b.Id == account.BankId).Select(b => b.Name).SingleAsync();

        await _audit.AppendAndSaveAsync(new NewAuditEntry(
            null, AuditActionTypes.PaymentCodeUsed, null, null,
            null, accountId.ToString(), null, null, "Success", null, requestingUserId));

        return new PayeeInfo(account.Id, account.Name, account.Currency, bankName);
    }
}
