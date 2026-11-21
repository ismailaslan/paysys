using Microsoft.EntityFrameworkCore;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Seeding;

// One-shot: `dotnet run --project src/Paysys.Api -- --create-account-for <username>` gives an
// existing user one account (balance 0.00, first bank, new account number) and exits. It does
// nothing if the user does not exist, has an account already, or there is no bank.
public static class AccountCreator
{
    private const int MaxAttempts = 5;

    public static async Task<string> RunAsync(PaysysDbContext db, string username)
    {
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.NormalizedUsername == User.Normalize(username));
        if (user is null)
        {
            return $"No user '{username}'.";
        }

        if (await db.Accounts.AnyAsync(a => a.OwnerId == user.Id))
        {
            return $"'{user.Username}' already has an account. Nothing changed.";
        }

        var bankId = await db.Banks.AsNoTracking().OrderBy(b => b.Id).Select(b => (int?)b.Id).FirstOrDefaultAsync();
        if (bankId is null)
        {
            return "No bank exists. Nothing changed.";
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var account = new Account(
                Guid.NewGuid(), $"{user.Username} Account", 0m, "USD",
                AccountType.Person, bankId.Value, ClientType.Individual, user.Id);
            var code = PayeeCodes.Generate();
            account.SetPayeeCode(code);
            db.Accounts.Add(account);
            try
            {
                await db.SaveChangesAsync();
                return $"Account created for '{user.Username}'. Account number: {code}";
            }
            catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, UniqueViolation.AccountPayeeCodeIndex))
            {
                db.ChangeTracker.Clear();
            }
        }

        return "Could not generate a unique account number. Nothing changed.";
    }
}
