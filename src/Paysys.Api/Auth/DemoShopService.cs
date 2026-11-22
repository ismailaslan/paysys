using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Auth;

// A built-in merchant for the public demo, so one sign-up is enough to try a whole payment. It is
// created the first time anyone asks, through the normal merchant registration, with a random
// password that nobody is given, so no one can sign in as it. Its payment codes come from the
// same in-memory store as any merchant's, and there is one active code per account, so the
// Send money page uses the code in the same moment it gets it.
public class DemoShopService
{
    public const string ShopUsername = "demo-shop";

    private readonly PaysysDbContext _db;
    private readonly UserRegistrationService _registration;
    private readonly PaymentCodeStore _store;

    public DemoShopService(PaysysDbContext db, UserRegistrationService registration, PaymentCodeStore store)
    {
        _db = db;
        _registration = registration;
        _store = store;
    }

    // Null when the shop has no account (the database has no bank yet).
    public async Task<PaymentCodeIssued?> IssueCodeAsync()
    {
        var accountId = await FindShopAccountAsync();
        if (accountId is null)
        {
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

            // Created, or UsernameTaken when a request at the same moment created it first.
            // Either way the shop exists afterwards, so the result itself does not matter.
            await _registration.RegisterAsync(ShopUsername, password, "merchant");
            accountId = await FindShopAccountAsync();
        }

        if (accountId is null)
            return null;

        var (code, expiresAt) = _store.Create(accountId.Value);
        return new PaymentCodeIssued(code, expiresAt);
    }

    private async Task<Guid?> FindShopAccountAsync()
    {
        var normalized = User.Normalize(ShopUsername);
        return await _db.Accounts.AsNoTracking()
            .Where(a => _db.Users.Any(u => u.NormalizedUsername == normalized && u.Id == a.OwnerId))
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync();
    }
}
