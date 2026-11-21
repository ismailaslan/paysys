using Microsoft.EntityFrameworkCore;
using Paysys.BLL.Services;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Seeding;

// One-shot: `dotnet run --project src/Paysys.Api -- --backfill-account-numbers` gives every
// account that has no payee code (the account number shown to its owner) a new one, then
// exits. It cannot tell an account that never had a code from one whose owner cleared it, so
// run it once. Each account is saved on its own; a clash with an existing code (about 1 in
// 2^50) is retried with a new one.
public static class AccountNumberBackfill
{
    private const int MaxAttempts = 5;

    public static async Task<int> RunAsync(PaysysDbContext db)
    {
        var ids = await db.Accounts.AsNoTracking()
            .Where(a => a.PayeeCode == null)
            .Select(a => a.Id)
            .ToListAsync();

        var added = 0;
        foreach (var id in ids)
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var account = await db.Accounts.FirstAsync(a => a.Id == id);
                account.SetPayeeCode(PayeeCodes.Generate());
                try
                {
                    await db.SaveChangesAsync();
                    added++;
                    break;
                }
                catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, UniqueViolation.AccountPayeeCodeIndex))
                {
                    db.ChangeTracker.Clear();
                }
            }
        }

        return added;
    }
}
