using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Auth;

// Self-registration: creates one Users row with role "user". Scoped, because it uses the
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
// The role is the constant User.UserRole. There is no role parameter anywhere on this path.
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

    private readonly PaysysDbContext _db;

    public UserRegistrationService(PaysysDbContext db)
    {
        _db = db;
    }

    public async Task<RegistrationResult> RegisterAsync(string? username, string? password)
    {
        // Length is checked before the regex runs, so a huge username costs nothing.
        if (username is null || username.Length > User.MaxUsernameLength || !UsernamePattern.IsMatch(username))
            return new RegistrationResult.Invalid(UsernameRuleMessage);

        if (password is null || password.Length < PasswordRules.MinLength || password.Length > PasswordRules.MaxLength)
            return new RegistrationResult.Invalid(PasswordRuleMessage);

        var hash = Hasher.HashPassword(HashingTemplate, password);
        var user = new User(Guid.NewGuid(), username, User.UserRole, hash);

        _db.Users.Add(user);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, UniqueViolation.UserNormalizedUsernameIndex))
        {
            // Someone already has this username (in any letter case), possibly registered a
            // moment ago by a concurrent request. The failed row is dropped from the context.
            _db.ChangeTracker.Clear();
            return new RegistrationResult.UsernameTaken();
        }

        return new RegistrationResult.Created(user.Id, user.Username);
    }
}

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
