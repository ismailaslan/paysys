using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Auth;

// Self-registration: creates one Users row with role "user" (an individual) or "merchant". Scoped, because it uses the
// request's PaysysDbContext. The endpoint applies the per-address rate limit before calling
// this; everything about what a valid account is lives here.
//
// In order:
// 1. username: ASCII letters, digits, '.', '_' and '-', 1-64 characters. This rules out
//    padding, look-alike letters (Cyrillic 'а'), characters that lower-case onto ASCII (the
//    Kelvin sign) and invisible characters;
// 2. password: PasswordRules.MinLength..MaxLength, no other rules, not trimmed;
// 3. hash it (PBKDF2, ASP.NET Core Identity format);
// 4. insert. There is no SELECT first: the unique index on NormalizedUsername decides, so
//    there is no check-then-insert window. Exactly that index violation becomes UsernameTaken;
//    any other failure propagates (a database outage must not look like "name taken").
//
// The role is User.UserRole or User.MerchantRole, chosen by the account kind. There is no role
// parameter anywhere on this path, so a caller can never become an admin or a bank.
// Nothing is written to the audit chain (decision Q3); the endpoint logs the new user's Id.
public class UserRegistrationService
{
    private static readonly PasswordHasher<User> Hasher = new();

    // PasswordHasher does not read the user it is given; this only satisfies the signature
    // before the real User exists (its constructor needs the hash).
    private static readonly User HashingTemplate = new(Guid.NewGuid(), "template", User.UserRole, "unused");

    // \z, not $: '$' would also match before a trailing newline.
    private static readonly Regex UsernamePattern = new(
        @"^[A-Za-z0-9._-]{1,64}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public const string UsernameRuleMessage =
        "Username must be 1 to 64 characters long and use only letters A-Z, digits, '.', '_' or '-'.";

    public static readonly string PasswordRuleMessage =
        $"Password must be between {PasswordRules.MinLength} and {PasswordRules.MaxLength} characters long.";

    // A demo limit on the whole user table, on top of the per-address rate limit, so the public
    // sign-up cannot be used to fill the database.
    public const int MaxUsers = 2000;

    // Every new user starts with one empty account.
    private const decimal StartingBalance = 0m;
    private const string StartingCurrency = "USD";
    private const int MaxAccountNumberAttempts = 5;

    private readonly PaysysDbContext _db;

    public UserRegistrationService(PaysysDbContext db)
    {
        _db = db;
    }

    public async Task<RegistrationResult> RegisterAsync(string? username, string? password, string? accountKind = null)
    {
        var isMerchant = string.Equals(accountKind, "merchant", StringComparison.OrdinalIgnoreCase);
        if (!isMerchant && !string.IsNullOrWhiteSpace(accountKind)
            && !string.Equals(accountKind, "individual", StringComparison.OrdinalIgnoreCase))
            return new RegistrationResult.Invalid("Account kind must be 'individual' or 'merchant'.");

        // Length is checked before the regex runs, so a huge username costs nothing.
        if (username is null || username.Length > User.MaxUsernameLength || !UsernamePattern.IsMatch(username))
            return new RegistrationResult.Invalid(UsernameRuleMessage);

        if (password is null || password.Length < PasswordRules.MinLength || password.Length > PasswordRules.MaxLength)
            return new RegistrationResult.Invalid(PasswordRuleMessage);

        if (await _db.Users.CountAsync() >= MaxUsers)
            return new RegistrationResult.Invalid("Sign-ups are closed: the demo has reached its limit of accounts.");

        var hash = Hasher.HashPassword(HashingTemplate, password);

        // The bank with the lowest Id hosts the new user's account. With no bank at all only
        // the user is created.
        var bankId = await _db.Banks.AsNoTracking().OrderBy(b => b.Id).Select(b => (int?)b.Id).FirstOrDefaultAsync();

        for (var attempt = 0; attempt < MaxAccountNumberAttempts; attempt++)
        {
            var user = new User(Guid.NewGuid(), username, isMerchant ? User.MerchantRole : User.UserRole, hash);
            _db.Users.Add(user);

            if (bankId is not null)
            {
                // Saved in the same SaveChanges as the user: both exist or neither does.
                // A merchant's account is a Business account with a terminal.
                var account = isMerchant
                    ? new Account(
                        Guid.NewGuid(), $"{username} Merchant Account", StartingBalance, StartingCurrency,
                        AccountType.Person, bankId.Value, ClientType.Business, user.Id, NewTerminalId())
                    : new Account(
                        Guid.NewGuid(), $"{username} Account", StartingBalance, StartingCurrency,
                        AccountType.Person, bankId.Value, ClientType.Individual, user.Id);
                account.SetPayeeCode(PayeeCodes.Generate());
                _db.Accounts.Add(account);
            }

            try
            {
                await _db.SaveChangesAsync();
                return new RegistrationResult.Created(user.Id, user.Username);
            }
            catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, UniqueViolation.UserNormalizedUsernameIndex))
            {
                // Someone already has this username (in any letter case), possibly registered a
                // moment ago by a concurrent request. The failed rows are dropped from the context.
                _db.ChangeTracker.Clear();
                return new RegistrationResult.UsernameTaken();
            }
            catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, UniqueViolation.AccountPayeeCodeIndex))
            {
                // The generated account number is already taken (about 1 in 2^50): try again
                // with a new one. Nothing was saved.
                _db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not generate a unique account number.");
    }

    // Admin only (the endpoint enforces it): a login with role "bank" and the bank's own account
    // (AccountType.Bank) at that bank. The bank role is tied to its bank through that account.
    public async Task<RegistrationResult> CreateBankLoginAsync(int bankId, string? username, string? password)
    {
        if (username is null || username.Length > User.MaxUsernameLength || !UsernamePattern.IsMatch(username))
            return new RegistrationResult.Invalid(UsernameRuleMessage);

        if (password is null || password.Length < PasswordRules.MinLength || password.Length > PasswordRules.MaxLength)
            return new RegistrationResult.Invalid(PasswordRuleMessage);

        var bank = await _db.Banks.AsNoTracking().SingleOrDefaultAsync(b => b.Id == bankId);
        if (bank is null)
            return new RegistrationResult.Invalid("That bank does not exist.");

        var user = new User(Guid.NewGuid(), username, User.BankRole, Hasher.HashPassword(HashingTemplate, password));
        _db.Users.Add(user);
        _db.Accounts.Add(new Account(
            Guid.NewGuid(), $"{bank.Name} Settlement", 0m, StartingCurrency,
            AccountType.Bank, bank.Id, ClientType.Individual, user.Id));

        try
        {
            await _db.SaveChangesAsync();
            return new RegistrationResult.Created(user.Id, user.Username);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, UniqueViolation.UserNormalizedUsernameIndex))
        {
            _db.ChangeTracker.Clear();
            return new RegistrationResult.UsernameTaken();
        }
    }

    // Admin only (the endpoint enforces it): sets a new password for a user, because there is no
    // email to send a reset link to. The password follows the same rules as at sign-up.
    public async Task<PasswordResetResult> ResetPasswordAsync(string? username, string? newPassword)
    {
        if (newPassword is null || newPassword.Length < PasswordRules.MinLength || newPassword.Length > PasswordRules.MaxLength)
            return new PasswordResetResult(PasswordResetOutcome.InvalidPassword, null);

        if (string.IsNullOrWhiteSpace(username))
            return new PasswordResetResult(PasswordResetOutcome.UserNotFound, null);

        var user = await _db.Users.SingleOrDefaultAsync(u => u.NormalizedUsername == User.Normalize(username));
        if (user is null)
            return new PasswordResetResult(PasswordResetOutcome.UserNotFound, null);

        user.ChangePasswordHash(Hasher.HashPassword(HashingTemplate, newPassword));
        await _db.SaveChangesAsync();
        return new PasswordResetResult(PasswordResetOutcome.Reset, user.Id);
    }

    // The hash a signed-in person's own password change stores; same hasher and rules as sign-up.
    public static string HashPassword(string password) => Hasher.HashPassword(HashingTemplate, password);

    // T-XXXXXX from letters and digits that are hard to confuse.
    public static string NewTerminalId() =>
        "T-" + RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 6);
}

public enum PasswordResetOutcome { Reset, InvalidPassword, UserNotFound }

public sealed record PasswordResetResult(PasswordResetOutcome Outcome, Guid? UserId);

public abstract record RegistrationResult
{
    private RegistrationResult()
    {
    }

    public sealed record Created(Guid Id, string Username) : RegistrationResult;

    // Message is safe to show to the caller: it states the rule, never the input.
    public sealed record Invalid(string Message) : RegistrationResult;

    public sealed record UsernameTaken : RegistrationResult;
}
