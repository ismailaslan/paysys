using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.BLL.Services;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.Shared.Profile;

namespace Paysys.Api.Endpoints;

// A person's own profile: what the system holds about them, their optional contact details, and their own
// password. The PIN is changed through POST /api/pin. Nothing here reads or changes anyone else's row: every
// endpoint works on the caller's own user id from the token.
public static class ProfileEndpoints
{
    // One @, something on both sides, a dot in the domain. Not verified: the address is only stored.
    private static readonly Regex EmailPattern = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // 7 to 15 digits, with an optional leading +. Spaces, dashes and brackets typed by the person are removed.
    private static readonly Regex PhonePattern = new(
        @"^\+?[0-9]{7,15}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static WebApplication MapProfileEndpoints(this WebApplication app)
    {
        app.MapGet("/api/profile", async (ClaimsPrincipal principal, PaysysDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId.Value);
            return user is null ? Results.Unauthorized() : Results.Ok(ToResponse(user));
        }).RequireAuthorization();

        app.MapPut("/api/profile/contact", async (UpdateContactRequest request, ClaimsPrincipal principal, PaysysDbContext db, TransactionAuditLogService audit) =>
        {
            var userId = principal.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var errors = new Dictionary<string, string[]>();

            var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
            if (email is not null && (email.Length > Paysys.DAL.Entities.User.MaxEmailLength || !EmailPattern.IsMatch(email)))
                errors["email"] = ["Enter an email address like name@example.com."];

            var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : CleanPhone(request.Phone);
            if (phone is not null && !PhonePattern.IsMatch(phone))
                errors["phone"] = ["Enter a phone number with 7 to 15 digits, for example +48 600 100 200."];

            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId.Value);
            if (user is null)
                return Results.Unauthorized();

            if (user.Email == email && user.Phone == phone)
                return Results.Ok(ToResponse(user));

            user.SetContact(email, phone);

            // The change and its audit entry are saved together. The entry says that the details changed, not what they are.
            await audit.AppendAndSaveAsync(new NewAuditEntry(
                null, AuditActionTypes.ContactChanged, null, null,
                null, null, null, null, "Success", null, userId.Value));
            return Results.Ok(ToResponse(user));
        }).RequireAuthorization();

        app.MapPost("/api/profile/password", async (ChangePasswordRequest request, ClaimsPrincipal principal, PaysysDbContext db,
            UserAuthenticator authenticator, LoginThrottle throttle, TransactionAuditLogService audit) =>
        {
            var userId = principal.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId.Value);
            if (user is null)
                return Results.Unauthorized();

            // The same lockout as sign-in, on the same counter: guessing the current password here is
            // no easier than guessing it at the login page.
            var key = LoginThrottle.Normalize(user.Username);
            var locked = throttle.GetLockRemaining(key);
            if (locked is not null)
                return new RetryAfterResult(locked.Value,
                    $"Too many failed attempts. Try again in {Math.Max(1, (int)Math.Ceiling(locked.Value.TotalSeconds))} seconds.");

            if (await authenticator.AuthenticateAsync(user.Username, request.CurrentPassword) is null)
            {
                throttle.RecordFailure(key);
                await audit.AppendAndSaveAsync(new NewAuditEntry(
                    null, AuditActionTypes.RejectedPassword, null, null,
                    null, null, null, null, AuditResults.Rejected, null, userId.Value));
                return Results.Json(new { message = "The current password is wrong." }, statusCode: StatusCodes.Status403Forbidden);
            }

            throttle.RecordSuccess(key);

            var next = request.NewPassword;
            if (next is null || next.Length < PasswordRules.MinLength || next.Length > PasswordRules.MaxLength)
                return Results.Json(new { message = UserRegistrationService.PasswordRuleMessage }, statusCode: StatusCodes.Status400BadRequest);

            if (next == request.CurrentPassword)
                return Results.Json(new { message = "Choose a password different from the current one." }, statusCode: StatusCodes.Status400BadRequest);

            user.ChangePasswordHash(UserRegistrationService.HashPassword(next));

            // The new hash and its audit entry are saved together. Neither carries the password.
            await audit.AppendAndSaveAsync(new NewAuditEntry(
                null, AuditActionTypes.PasswordChanged, null, null,
                null, null, null, null, "Success", null, userId.Value));
            return Results.NoContent();
        }).RequireAuthorization();

        // Ends every token issued to the caller, on every device, including the one that asks. The password is asked
        // again, with the same lockout as sign-in, so a borrowed session cannot do it and cannot be used to guess the
        // password. The raise is one UPDATE (TokenVersion + 1); every request checks it.
        app.MapPost("/api/profile/sign-out-everywhere", async (SignOutEverywhereRequest request, ClaimsPrincipal principal, PaysysDbContext db,
            UserAuthenticator authenticator, LoginThrottle throttle, TransactionAuditLogService audit) =>
        {
            var userId = principal.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId.Value);
            if (user is null)
                return Results.Unauthorized();

            var key = LoginThrottle.Normalize(user.Username);
            var locked = throttle.GetLockRemaining(key);
            if (locked is not null)
                return new RetryAfterResult(locked.Value,
                    $"Too many failed attempts. Try again in {Math.Max(1, (int)Math.Ceiling(locked.Value.TotalSeconds))} seconds.");

            if (request.Password is null || await authenticator.AuthenticateAsync(user.Username, request.Password) is null)
            {
                throttle.RecordFailure(key);
                await audit.AppendAndSaveAsync(new NewAuditEntry(
                    null, AuditActionTypes.RejectedPassword, null, null,
                    null, null, null, null, AuditResults.Rejected, null, userId.Value));
                return Results.Json(new { message = "The password is wrong." }, statusCode: StatusCodes.Status403Forbidden);
            }

            throttle.RecordSuccess(key);

            await db.Users.Where(u => u.Id == userId.Value)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.TokenVersion, u => u.TokenVersion + 1));

            await audit.AppendAndSaveAsync(new NewAuditEntry(
                null, AuditActionTypes.SignedOutEverywhere, null, null,
                null, null, null, null, "Success", null, userId.Value));
            return Results.NoContent();
        }).RequireAuthorization();

        // Everything the system holds about the caller, as a JSON file: profile, accounts, cards, payments and
        // their own activity. No password or PIN hash, and no card number (it is never stored).
        app.MapGet("/api/profile/export", async (ClaimsPrincipal principal, PaysysDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId.Value);
            if (user is null)
                return Results.Unauthorized();

            var accounts = await db.Accounts.AsNoTracking().Where(a => a.OwnerId == user.Id).OrderBy(a => a.CreatedAt).ToListAsync();
            var ids = accounts.Select(a => a.Id).ToList();

            var cards = await db.CardTokens.AsNoTracking().Where(c => ids.Contains(c.AccountId)).OrderBy(c => c.CreatedAt).ToListAsync();
            var payments = await db.Transactions.AsNoTracking()
                .Where(t => ids.Contains(t.SourceAccountId) || ids.Contains(t.DestinationAccountId))
                .OrderByDescending(t => t.CreatedAt).Take(ExportLimit).ToListAsync();
            var activity = await db.TransactionAuditLogs.AsNoTracking()
                .Where(a => a.ActorUserId == user.Id)
                .OrderByDescending(a => a.TimestampUtc).Take(ExportLimit)
                .Select(a => new { a.TimestampUtc, a.ActionType, a.Result, a.Amount, a.Currency })
                .ToListAsync();

            var export = new
            {
                exportedAt = DateTimeOffset.UtcNow,
                note = "Everything Paysys holds about you. Not included: your password and PIN (stored only as one-way hashes) and full card numbers (never stored). Lists are limited to the newest " + ExportLimit + " entries.",
                profile = new { user.Username, user.Role, user.CreatedAt, user.Email, user.Phone, hasPin = user.PinHash is not null },
                accounts = accounts.Select(a => new
                {
                    a.Id, a.Name, a.Currency, a.Balance, type = a.AccountType.ToString(), client = a.ClientType.ToString(),
                    a.BankId, a.TerminalId, a.PayeeCode, a.CreatedAt,
                }),
                cards = cards.Select(c => new
                {
                    c.Id, c.AccountId, c.LastFourDigits, brand = c.CardBrand.ToString(), c.ExpiryMonth, c.ExpiryYear, status = c.Status.ToString(), c.CreatedAt,
                }),
                payments = payments.Select(t => new
                {
                    t.Id, t.Amount, t.Currency, t.ConvertedAmount, t.ConvertedCurrency, t.ExchangeRate, status = t.Status.ToString(),
                    t.SourceAccountId, t.DestinationAccountId, t.FailureReason, t.CreatedAt,
                }),
                activity,
            };

            return Results.Text(JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }), "application/json");
        }).RequireAuthorization();

        // Erasure at the person's own request. Personal details and the way to sign in are removed; accounts,
        // payments and audit entries stay (financial records), pointing at an anonymous row. Only an individual
        // can do it here, and only with empty accounts so no money is left behind.
        app.MapPost("/api/profile/delete", async (DeleteAccountRequest request, ClaimsPrincipal principal, PaysysDbContext db,
            UserAuthenticator authenticator, LoginThrottle throttle, TransactionAuditLogService audit) =>
        {
            var userId = principal.GetUserId();
            if (userId is null)
                return Results.Unauthorized();

            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId.Value);
            if (user is null)
                return Results.Unauthorized();

            if (user.Role != Paysys.DAL.Entities.User.UserRole)
                return Results.Json(new { message = "Only an individual account can be deleted here. Ask the operator to close a merchant, bank or admin account." },
                    statusCode: StatusCodes.Status403Forbidden);

            var key = LoginThrottle.Normalize(user.Username);
            var locked = throttle.GetLockRemaining(key);
            if (locked is not null)
                return new RetryAfterResult(locked.Value,
                    $"Too many failed attempts. Try again in {Math.Max(1, (int)Math.Ceiling(locked.Value.TotalSeconds))} seconds.");

            if (await authenticator.AuthenticateAsync(user.Username, request.Password) is null)
            {
                throttle.RecordFailure(key);
                await audit.AppendAndSaveAsync(new NewAuditEntry(
                    null, AuditActionTypes.RejectedPassword, null, null,
                    null, null, null, null, AuditResults.Rejected, null, userId.Value));
                return Results.Json(new { message = "The password is wrong." }, statusCode: StatusCodes.Status403Forbidden);
            }

            throttle.RecordSuccess(key);

            var accounts = await db.Accounts.Where(a => a.OwnerId == user.Id).ToListAsync();
            var ids = accounts.Select(a => a.Id).ToList();

            if (accounts.Any(a => a.Balance != 0m))
                return Results.Json(new { message = "Your accounts still hold money. Send it away first, then delete your account." },
                    statusCode: StatusCodes.Status409Conflict);

            foreach (var account in accounts)
                account.SetPayeeCode(null);

            var cards = await db.CardTokens
                .Where(c => ids.Contains(c.AccountId) && c.Status != CardStatus.Blocked && c.Status != CardStatus.Closed)
                .ToListAsync();
            foreach (var card in cards)
                card.Close();

            user.Anonymize(UserRegistrationService.HashPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));

            // Everything is saved with its audit entry, which says that an account was erased and carries no details.
            await audit.AppendAndSaveAsync(new NewAuditEntry(
                null, AuditActionTypes.AccountDeleted, null, null,
                null, null, null, null, "Success", null, userId.Value));
            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }

    private const int ExportLimit = 5000;

    private static string CleanPhone(string phone) =>
        new string(phone.Where(c => c is not (' ' or '-' or '(' or ')' or '.')).ToArray());

    private static ProfileResponse ToResponse(Paysys.DAL.Entities.User user) =>
        new(user.Username, user.Role, user.CreatedAt, user.PinHash is not null, user.Email, user.Phone, user.TotpEnabledAt is not null);
}
