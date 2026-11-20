using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.Tokenization;

namespace Paysys.Api.Seeding;

// Loads sample data so a visitor lands on something that works: two banks, a few accounts
// and two cards. Opt-in only - it runs when the API is started with --seed-demo, then exits:
//
//   dotnet run --project src/Paysys.Api -- --seed-demo
//
// It refuses to run unless the database has no banks at all, so it can never add to, or
// collide with, real data. If a run fails part-way the database is no longer empty and the
// seed will not run again until the partial data is cleared.
//
// Accounts are owned by users from Auth:Users: the demo accounts by the first user with the
// "user" role (one must be configured), the shop and the second bank's account by the first
// admin. Cards use the card networks' published test numbers, and go through the normal
// tokenization service, so only a token and the last four digits are stored.
//
// Optional setting Seed:SecondBankEndpoint gives "Demo Bank Two" an approval endpoint so
// transfers between the two banks can be tried. It is checked when a call is made, like any
// bank endpoint. Without it, transfers to Bank Two fail closed and are recorded as failures.
public static class DemoSeeder
{
    private const string TestVisa = "4111111111111111";
    private const string TestMastercard = "5555555555554444";

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<PaysysDbContext>();
        var auth = services.GetRequiredService<AuthOptions>();
        var configuration = services.GetRequiredService<IConfiguration>();
        var tokenization = services.GetRequiredService<CardTokenizationService>();

        if (await db.Banks.AnyAsync())
        {
            Console.Error.WriteLine("Demo seed not run: the database already contains banks. It only runs on an empty database.");
            return 1;
        }

        var demoUser = auth.Users.FirstOrDefault(u => u.Role == AuthRoles.User);
        if (demoUser is null)
        {
            Console.Error.WriteLine("Demo seed not run: add a user with Role \"user\" to Auth:Users first (with a PasswordHash).");
            return 1;
        }

        var adminUser = auth.Users.FirstOrDefault(u => u.Role == AuthRoles.Admin) ?? demoUser;

        var secondBankEndpoint = configuration["Seed:SecondBankEndpoint"];
        if (string.IsNullOrWhiteSpace(secondBankEndpoint))
            secondBankEndpoint = null;

        var bankOne = new Bank("Demo Bank One", "DEMO1");
        var bankTwo = new Bank("Demo Bank Two", "DEMO2", secondBankEndpoint);
        db.Banks.AddRange(bankOne, bankTwo);
        await db.SaveChangesAsync();

        var checking = new Account(
            Guid.NewGuid(), "Demo Checking", 5000m, "EUR",
            AccountType.Person, bankOne.Id, ClientType.Individual, demoUser.Id);

        var savings = new Account(
            Guid.NewGuid(), "Demo Savings", 2500m, "USD",
            AccountType.Person, bankOne.Id, ClientType.Individual, demoUser.Id);

        var shop = new Account(
            Guid.NewGuid(), "Demo Shop", 0m, "USD",
            AccountType.Person, bankOne.Id, ClientType.Business, adminUser.Id, terminalId: "TERM-0001");
        var shopPayeeCode = PayeeCodes.Generate();
        shop.SetPayeeCode(shopPayeeCode);

        var bankTwoAccount = new Account(
            Guid.NewGuid(), "Bank Two Account", 0m, "GBP",
            AccountType.Person, bankTwo.Id, ClientType.Individual, adminUser.Id);

        db.Accounts.AddRange(checking, savings, shop, bankTwoAccount);
        await db.SaveChangesAsync();

        var expiryYear = DateTime.UtcNow.Year + 4;
        await tokenization.TokenizeAsync(demoUser.Id, checking.Id, TestVisa, 12, expiryYear);
        await tokenization.TokenizeAsync(demoUser.Id, savings.Id, TestMastercard, 12, expiryYear);

        Console.WriteLine("Demo data loaded.");
        Console.WriteLine($"  Banks:    {bankOne.Name}, {bankTwo.Name}{(secondBankEndpoint is null ? " (no approval endpoint set)" : "")}");
        Console.WriteLine($"  Accounts: {checking.Name} (EUR), {savings.Name} (USD) owned by '{demoUser.Username}'");
        Console.WriteLine($"            {shop.Name} (USD, business), {bankTwoAccount.Name} (GBP) owned by '{adminUser.Username}'");
        Console.WriteLine($"  Cards:    one test Visa on {checking.Name}, one test Mastercard on {savings.Name}");
        Console.WriteLine($"  Shop payee code: {shopPayeeCode}");
        return 0;
    }
}
