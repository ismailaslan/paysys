using Microsoft.EntityFrameworkCore;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Auth;

// One-shot copy of the Auth:Users configuration entries into the Users table. Run by hand:
//
//   dotnet run --project src/Paysys.Api -- --import-users
//
// Rules:
// - Every entry is checked first (Id, Username, Role, PasswordHash, no duplicates). One bad
//   entry stops the import and nothing is written.
// - Ids are copied unchanged, so existing Account.OwnerId and audit ActorUserId values keep
//   pointing at the same person.
// - A row that already exists with the same Id and the same username is skipped. Its stored
//   hash is never touched, even if configuration now holds a different one.
// - An Id that exists under another username, or a username that exists under another Id,
//   stops the whole import and nothing is written. Picking either side quietly could hand
//   accounts to the wrong person.
// - All reads and writes happen in one database transaction.
// - Output names users by username and Id only. Password hashes are never written anywhere.
// - Nothing is written to the audit chain; the console output is the record of the run.
public class ConfigUserImporter
{
    private readonly PaysysDbContext _db;
    private readonly AuthOptions _options;

    public ConfigUserImporter(PaysysDbContext db, AuthOptions options)
    {
        _db = db;
        _options = options;
    }

    // For the command line: prints the outcome and returns the process exit code.
    public async Task<int> RunAsync(TextWriter output, TextWriter error)
    {
        try
        {
            var result = await ImportAsync();

            foreach (var user in result.Added)
                output.WriteLine($"  added    {user.Username} ({user.Id})");
            foreach (var user in result.Skipped)
                output.WriteLine($"  skipped  {user.Username} ({user.Id}) - already in the Users table");
            output.WriteLine($"User import done: {result.Added.Count} added, {result.Skipped.Count} skipped.");
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
    public async Task<UserImportResult> ImportAsync()
    {
        var entries = ValidateConfiguration(_options.Users);

        await using var transaction = await _db.Database.BeginTransactionAsync();

        var ids = entries.Select(e => e.Id).ToList();
        var normalizedNames = entries.Select(e => User.Normalize(e.Username)).ToList();

        // Every existing row that shares an Id or a username with any configured entry.
        var existing = await _db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id) || normalizedNames.Contains(u.NormalizedUsername))
            .Select(u => new { u.Id, u.Username, u.NormalizedUsername })
            .ToListAsync();

        var added = new List<ImportedUser>();
        var skipped = new List<ImportedUser>();

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

            if (byId is not null)
            {
                // Same Id, same username: already imported. The stored hash is left alone.
                skipped.Add(new ImportedUser(entry.Id, entry.Username));
                continue;
            }

            _db.Users.Add(new User(entry.Id, entry.Username, entry.Role, entry.PasswordHash));
            added.Add(new ImportedUser(entry.Id, entry.Username));
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Most likely another import inserted the same Id or username after the read above.
            // The exception text is not printed: keep the output to usernames and Ids.
            _db.ChangeTracker.Clear();
            throw new UserImportException(
                "the database rejected the new rows (a user with the same Id or username was added concurrently?). Run the import again to see which one.");
        }

        await transaction.CommitAsync();
        return new UserImportResult(added, skipped);
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
        }

        if (users.Select(u => User.Normalize(u.Username)).Distinct().Count() != users.Count)
            throw new UserImportException("Auth:Users contains duplicate usernames (compared without case).");

        if (users.Select(u => u.Id).Distinct().Count() != users.Count)
            throw new UserImportException("Auth:Users contains duplicate Ids.");

        return users;
    }
}

public sealed record ImportedUser(Guid Id, string Username);

public sealed record UserImportResult(IReadOnlyList<ImportedUser> Added, IReadOnlyList<ImportedUser> Skipped);

// A reason the import was refused. Messages contain usernames, Ids and config paths only.
public sealed class UserImportException(string message) : Exception(message);
