using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Paysys.Api.Auth;
using Paysys.BLL.Services;
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

        return app;
    }

    private static string CleanPhone(string phone) =>
        new string(phone.Where(c => c is not (' ' or '-' or '(' or ')' or '.')).ToArray());

    private static ProfileResponse ToResponse(Paysys.DAL.Entities.User user) =>
        new(user.Username, user.Role, user.CreatedAt, user.PinHash is not null, user.Email, user.Phone);
}
