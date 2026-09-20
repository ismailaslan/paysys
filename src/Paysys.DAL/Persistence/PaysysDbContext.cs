using Microsoft.EntityFrameworkCore;
using Paysys.DAL.Entities;

namespace Paysys.DAL.Persistence;

public class PaysysDbContext : DbContext
{
    public PaysysDbContext(DbContextOptions<PaysysDbContext> options)
        : base(options)
    {
    }

    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<CardToken> CardTokens => Set<CardToken>();
    public DbSet<Bank> Banks => Set<Bank>();
    public DbSet<FraudCheckLedger> FraudCheckLedgers => Set<FraudCheckLedger>();
    public DbSet<TransactionAuditLog> TransactionAuditLogs => Set<TransactionAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.Property(t => t.Amount).HasPrecision(18, 2);
            entity.Property(t => t.Currency).HasMaxLength(3);
            entity.Property(t => t.ConvertedAmount).HasPrecision(18, 2);
            entity.Property(t => t.ConvertedCurrency).HasMaxLength(3);
            entity.Property(t => t.ExchangeRate).HasPrecision(18, 6);
            entity.Property(t => t.IdempotencyKey).HasMaxLength(255);
            entity.Property(t => t.FailureReason).HasMaxLength(1000);

            // CardTokenId is audit metadata only (which card authorized this transfer),
            // not a funding source or a relationship EF needs to traverse - same
            // no-navigation-property, no-FK-constraint treatment as SourceAccountId/
            // DestinationAccountId above; referential integrity is enforced by
            // TransactionProcessingService, not the database.
            entity.Property(t => t.CardTokenId);

            entity.HasIndex(t => t.IdempotencyKey).IsUnique();
        });

        modelBuilder.Entity<Account>(entity =>
        {
            entity.Property(a => a.Balance).HasPrecision(18, 2);
            entity.Property(a => a.Currency).HasMaxLength(3);
            entity.Property(a => a.TerminalId).HasMaxLength(50);

            // OwnerId is a user Id from the API's configuration-seeded users - there
            // is no users table, so this is deliberately not a foreign key. Ownership
            // is enforced by CardTokenizationService and TransactionProcessingService.
            entity.Property(a => a.OwnerId);
            entity.HasIndex(a => a.OwnerId);

            // Unique, so a code identifies exactly one account. Postgres allows any number
            // of NULLs in a unique index, which is what "no code" is.
            entity.Property(a => a.PayeeCode).HasMaxLength(20);
            entity.HasIndex(a => a.PayeeCode).IsUnique();

            // Postgres's own MVCC system column; the engine updates it on every
            // row write, unlike EF's IsRowVersion() on a plain byte[] column,
            // which Npgsql cannot auto-generate.
            entity.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

            // Unlike SourceAccountId/DestinationAccountId/CardTokenId elsewhere in
            // this model, BankId is a genuine relational FK to a small reference
            // table - Restrict so a Bank can never be deleted while accounts still
            // reference it.
            entity.HasOne<Bank>()
                .WithMany()
                .HasForeignKey(a => a.BankId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Bank>(entity =>
        {
            entity.Property(b => b.Name).HasMaxLength(200).IsRequired();
            entity.Property(b => b.BankCode).HasMaxLength(20).IsRequired();
            entity.Property(b => b.ApiEndpoint).HasMaxLength(500);

            entity.HasIndex(b => b.BankCode).IsUnique();

            // Seeded reference data (see AddBank migration). Names deliberately
            // avoid colliding with the pre-existing Account rows literally named
            // "Bank A"/"Bank B" (accountType=Bank) from before this table existed.
            // ApiEndpoint points at this same Api process's own bank-approval stub
            // (see AddBankApiEndpoint migration) - a real bank would have an actual
            // external URL here; this loopback address is a dev-only simplification
            // so the stub demonstrates the routing contract without a second service.
            entity.HasData(
                new { Id = 1, Name = "First National", BankCode = "BANKA", ApiEndpoint = "http://localhost:5121/api/stub/bank-approval" },
                new { Id = 2, Name = "Continental Trust", BankCode = "BANKB", ApiEndpoint = "http://localhost:5121/api/stub/bank-approval" });
        });

        modelBuilder.Entity<CardToken>(entity =>
        {
            entity.Property(c => c.Token).HasMaxLength(255);
            entity.Property(c => c.LastFourDigits).HasMaxLength(4);

            // AccountId ownership is enforced by CardTokenizationService (on create)
            // and TransactionProcessingService (on use) - same no-navigation-property,
            // no-FK-constraint treatment as every other cross-entity Guid in this model.
            entity.Property(c => c.AccountId);

            entity.HasIndex(c => c.Token).IsUnique();
        });

        modelBuilder.Entity<FraudCheckLedger>(entity =>
        {
            entity.Property(f => f.Amount).HasPrecision(18, 2);

            // AccountId is audit-metadata style, same no-FK treatment as elsewhere
            // in this model. TransactionId IS a real FK per the explicit spec for
            // this table - Restrict, and unique since this design writes exactly
            // one ledger row per transaction attempt.
            entity.Property(f => f.AccountId);

            entity.HasOne<Transaction>()
                .WithMany()
                .HasForeignKey(f => f.TransactionId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(f => f.TransactionId).IsUnique();
            entity.HasIndex(f => new { f.AccountId, f.TimestampUtc });
        });

        modelBuilder.Entity<TransactionAuditLog>(entity =>
        {
            entity.Property(a => a.TimestampLocal).HasColumnType("timestamp without time zone");
            entity.Property(a => a.ActionType).HasMaxLength(50);
            entity.Property(a => a.Amount).HasPrecision(18, 2);
            entity.Property(a => a.Currency).HasMaxLength(3);
            entity.Property(a => a.FromAccountRef).HasMaxLength(255);
            entity.Property(a => a.ToAccountRef).HasMaxLength(255);
            entity.Property(a => a.CardTokenRef).HasMaxLength(255);
            entity.Property(a => a.TerminalId).HasMaxLength(50);
            entity.Property(a => a.Result).HasMaxLength(20);
            entity.Property(a => a.FlagReason).HasMaxLength(200);

            // Not a FK: users are configuration-seeded, there is no users table.
            // Indexed so "everything this user was denied" is a cheap query.
            entity.Property(a => a.ActorUserId);
            entity.HasIndex(a => a.ActorUserId);
            entity.Property(a => a.Hash).HasMaxLength(64);
            entity.Property(a => a.PreviousHash).HasMaxLength(64);

            // TransactionId is a real FK, but nullable - not every audit entry is
            // transaction-tied (e.g. a cross-bank routing attempt that failed
            // before any Transaction row was ever created), and future action
            // types (account/card creation) won't be either.
            entity.HasOne<Transaction>()
                .WithMany()
                .HasForeignKey(a => a.TransactionId)
                .OnDelete(DeleteBehavior.Restrict);

            // PreviousEntryId (not a DB identity column) defines chain order -
            // see the type's own comment for why. Self-referencing FK, nullable
            // (the genesis entry has none), Restrict so an entry can never be
            // deleted while a later entry still points to it.
            entity.HasOne<TransactionAuditLog>()
                .WithMany()
                .HasForeignKey(a => a.PreviousEntryId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(a => a.PreviousEntryId).IsUnique();
            entity.HasIndex(a => a.TransactionId);
        });
    }
}
