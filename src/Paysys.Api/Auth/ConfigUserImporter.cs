using Microsoft.EntityFrameworkCore;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Auth;

// One-shot copy of the Auth:Users configuration entries into the Users table. Run by hand:
//
//   dotnet run --project src/Paysys.Api -- --import-users
//   dotnet run --project src/Paysys.Api -- --import-users --update-existing
//
// Rules:
// - Every entry is checked first (Id, Username, Role, PasswordHash format, no duplicates). One
//   bad entry stops the import and nothing is written.
// - Ids are copied unchanged, so existing Account.OwnerId and audit ActorUserId values keep
//   pointing at the same person.
// - Without --update-existing, a row that already exists with the same Id and the same
//   username is skipped. Its stored hash and role are never touched, even if configuration now
//   holds different ones.
// - With --update-existing, such a row is matched instead, and only its Role and PasswordHash
//   may change (compared as plain strings; promotion to admin is allowed). Id, Username
//   (including its casing), NormalizedUsername and CreatedAt never change. A matched row that
//   already equals configuration is reported as unchanged.
// - An Id that exists under another username, or a username that exists under another Id,
//   stops the whole import and nothing is written, in both modes. Picking either side quietly
//   could hand accounts to the wrong person.
// - Rows that are not in configuration (for example self-registered users) are never touched.
// - If the run would demote at least one admin and leave no admin row in the table, the whole
//   import stops and nothing is written. The count is read inside the import's transaction; a
//   second import running at the same moment is not locked out.
// - All reads and writes happen in one database transaction.
// - Output names users by username and Id, plus the names of changed fields. Password hashes
//   and role values are never written anywhere.
// - Nothing is written to the audit chain; the console output is the record of the run.
public class ConfigUserImporter
{
    public const string RoleField = "role";
    public const string PasswordHashField = "password hash";

    private readonly PaysysDbContext _db;
    private readonly AuthOptions _options;

    public ConfigUserImporter(PaysysDbContext db, AuthOptions options)
    {
        _db = db;
        _options = options;
    }

    // For the command line: prints the outcome and returns the process exit code.
    public async Task<int> RunAsync(TextWriter output, TextWriter error, bool updateExisting = false)
    {
        try
        {
            var result = await ImportAsync(updateExisting);

            foreach (var user in result.Added)
                output.WriteLine($"  added    {user.Username} ({user.Id})");
            foreach (var user in result.Skipped)
                output.WriteLine($"  skipped  {user.Username} ({user.Id}) - already in the Users table");
            foreach (var user in result.Updated)
                output.WriteLine($"  updated  {user.Username} ({user.Id}) - changed: {string.Join(", ", user.ChangedFields)}");
            foreach (var user in result.Unchanged)
                output.WriteLine($"  unchanged {user.Username} ({user.Id})");

            output.WriteLine(updateExisting
                ? $"User import done: {result.Added.Count} added, {result.Updated.Count} updated, {result.Unchanged.Count} unchanged."
                : $"User import done: {result.Added.Count} added, {result.Skipped.Count} skipped.");
            return 0;
        }
        catch (UserImportException ex)
        {
            error.WriteLine($"User import stopped, nothing was written: {ex.Message}");
            return 1;
        }
    }

    // Throws UserImportException (and writes nothing) when the configuration or the existing
    // table rows do not allow a clean import.
    public async Task<UserImportResult> ImportAsync(bool updateExisting = false)
    {
        var entries = ValidateConfiguration(_options.Users);

        await using var transaction = await _db.Database.BeginTransactionAsync();

        var ids = entries.Select(e => e.Id).ToList();
        var normalizedNames = entries.Select(e => User.Normalize(e.Username)).ToList();

        // Every existing row that shares an Id or a username with any configured entry. Tracked
        // only in update mode, where matched rows may be changed and saved.
        var source = updateExisting ? _db.Users : _db.Users.AsNoTracking();
        var existing = await source
            .Where(u => ids.Contains(u.Id) || normalizedNames.Contains(u.NormalizedUsername))
            .ToListAsync();

        var added = new List<ImportedUser>();
        var skipped = new List<ImportedUser>();
        var updated = new List<UpdatedUser>();
        var unchanged = new List<ImportedUser>();
        var demotions = 0;
        var promotions = 0;
        var addedAdmins = 0;

        foreach (var entry in entries)
        {
            var normalized = User.Normalize(entry.Username);
            var byId = existing.FirstOrDefault(u => u.Id == entry.Id);
            var byName = existing.FirstOrDefault(u => u.NormalizedUsername == normalized);

            if (byId is not null && byId.NormalizedUsername != normalized)
                throw new UserImportException(
                    $"Id {entry.Id} is configured for '{entry.Username}' but the Users table has it as '{byId.Username}'.");

            if (byName is not null && byName.Id != entry.Id)
                throw new UserImportException(
                    $"Username '{entry.Username}' is configured with Id {entry.Id} but the Users table has it under Id {byName.Id}.");

            if (byId is not null && !updateExisting)
            {
                // Same Id, same username: already imported. The stored hash is left alone.
                skipped.Add(new ImportedUser(entry.Id, entry.Username));
                continue;
            }

            if (byId is not null)
            {
                // Update mode, same Id and same username. Only Role and PasswordHash may change.
                var changed = new List<string>();
                if (byId.Role != entry.Role)
                    changed.Add(RoleField);
                if (byId.PasswordHash != entry.PasswordHash)
                    changed.Add(PasswordHashField);

                if (changed.Count == 0)
                {
                    unchanged.Add(new ImportedUser(entry.Id, entry.Username));
                    continue;
                }

                if (byId.Role == User.AdminRole && entry.Role == User.UserRole)
                    demotions++;
                else if (byId.Role == User.UserRole && entry.Role == User.AdminRole)
                    promotions++;

                byId.UpdateRoleAndPasswordHash(entry.Role, entry.PasswordHash);
                updated.Add(new UpdatedUser(entry.Id, entry.Username, changed));
                continue;
            }

            _db.Users.Add(new User(entry.Id, entry.Username, entry.Role, entry.PasswordHash));
            added.Add(new ImportedUser(entry.Id, entry.Username));
            if (entry.Role == User.AdminRole)
                addedAdmins++;
        }

        // Last-admin refusal. Only checked when this run demotes someone, so importing a
        // user-only configuration into a table that never had an admin is still allowed. The
        // count is the table as it is now inside this transaction (nothing has been saved yet).
        if (demotions > 0)
        {
            var currentAdmins = await _db.Users.CountAsync(u => u.Role == User.AdminRole);
            var remainingAdmins = currentAdmins - demotions + promotions + addedAdmins;
            if (remainingAdmins <= 0)
            {
                _db.ChangeTracker.Clear();
                throw new UserImportException("the import would leave no admin user in the Users table; nothing was written.");
            }
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Most likely another import inserted the same Id or username after the read above
            // (or, in update mode, changed or removed a matched row). The exception text is not
            // printed: keep the output to usernames and Ids.
            _db.ChangeTracker.Clear();
            throw new UserImportException(updateExisting
                ? "the database rejected the changes (a user with the same Id or username was added, or a matched row was removed, concurrently?). Run the import again to see which one."
                : "the database rejected the new rows (a user with the same Id or username was added concurrently?). Run the import again to see which one.");
        }

        await transaction.CommitAsync();
        return new UserImportResult(added, skipped, updated, unchanged);
    }

    // The checks that used to run at API startup, plus the limits the table needs.
    private static List<DemoUser> ValidateConfiguration(List<DemoUser> users)
    {
        if (users.Count == 0)
            throw new UserImportException("Auth:Users is empty; there is nothing to import.");

        for (var i = 0; i < users.Count; i++)
        {
            var user = users[i];
            var label = $"Auth:Users:{i}";

            if (user.Id == Guid.Empty)
                throw new UserImportException($"{label} has no Id.");

            if (string.IsNullOrWhiteSpace(user.Username))
                throw new UserImportException($"{label} has no Username.");

            if (user.Username.Trim() != user.Username)
                throw new UserImportException($"{label} Username starts or ends with whitespace.");

            if (user.Username.Length > User.MaxUsernameLength)
                throw new UserImportException($"{label} Username is longer than {User.MaxUsernameLength} characters.");

            if (user.Role is not (AuthRoles.Admin or AuthRoles.User))
                throw new UserImportException($"User '{user.Username}' has invalid Role '{user.Role}'. Use 'admin' or 'user'.");

            if (string.IsNullOrEmpty(user.PasswordHash))
                throw new UserImportException(
                    $"User '{user.Username}' has no PasswordHash. Set {label}:PasswordHash in user-secrets.");

            if (user.PasswordHash.Length > User.MaxPasswordHashLength)
                throw new UserImportException(
                    $"User '{user.Username}' has a PasswordHash longer than {User.MaxPasswordHashLength} characters.");

            // Login cannot read a hash that is not base64 or has an unknown format marker
            // (review finding 2), so such a hash is refused here, before anything is written.
            // The message never contains the hash itself.
            if (!PasswordHashFormat.IsWellFormed(user.PasswordHash))
                throw new UserImportException(
                    $"User '{user.Username}' has a PasswordHash that is not an ASP.NET Core Identity PasswordHasher hash (not base64, or an unknown format marker).");
        }

        if (users.Select(u => User.Normalize(u.Username)).Distinct().Count() != users.Count)
            throw new UserImportException("Auth:Users contains duplicate usernames (compared without case).");

        if (users.Select(u => u.Id).Distinct().Count() != users.Count)
            throw new UserImportException("Auth:Users contains duplicate Ids.");

        return users;
    }
}

public sealed record ImportedUser(Guid Id, string Username);

// ChangedFields holds field names only (ConfigUserImporter.RoleField, PasswordHashField), never values.
public sealed record UpdatedUser(Guid Id, string Username, IReadOnlyList<string> ChangedFields);

// Skipped is filled only without --update-existing; Updated and Unchanged only with it.
public sealed record UserImportResult(
    IReadOnlyList<ImportedUser> Added,
    IReadOnlyList<ImportedUser> Skipped,
    IReadOnlyList<UpdatedUser> Updated,
    IReadOnlyList<ImportedUser> Unchanged);

// A reason the import was refused. Messages contain usernames, Ids and config paths only.
public sealed class UserImportException(string message) : Exception(message);
