using Microsoft.EntityFrameworkCore;
using Paysys.Domain.Entities;

namespace Paysys.Infrastructure.Persistence;

public class PaysysDbContext : DbContext
{
    public PaysysDbContext(DbContextOptions<PaysysDbContext> options)
        : base(options)
    {
    }

    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<CardToken> CardTokens => Set<CardToken>();

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

            // Postgres's own MVCC system column; the engine updates it on every
            // row write, unlike EF's IsRowVersion() on a plain byte[] column,
            // which Npgsql cannot auto-generate.
            entity.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();
        });

        modelBuilder.Entity<CardToken>(entity =>
        {
            entity.Property(c => c.Token).HasMaxLength(255);
            entity.Property(c => c.LastFourDigits).HasMaxLength(4);

            entity.HasIndex(c => c.Token).IsUnique();
        });
    }
}
