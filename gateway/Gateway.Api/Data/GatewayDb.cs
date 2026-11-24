using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Data;

// One terminal that may ask for payments. Only a hash of its key is kept.
public sealed class Terminal
{
    public string TerminalId { get; set; } = "";
    public string BankCode { get; set; } = "";
    public Guid AccountId { get; set; }
    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

// A payment or a hold a terminal asked for. Kind is Payment or Hold.
// Payment status: Pending, Completed, Error (the bank refused it, FailureReason says why) or Expired.
// Hold status: Pending, Held (the payer reserved the money), Captured, Released, Expired or Error.
// A hold's own id at the bank is HoldRef; CapturedAmount is what the terminal took, in the terminal's currency.
public sealed class GatewayPayment
{
    public Guid Id { get; set; }
    public string TerminalId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Status { get; set; } = "Pending";
    public Guid? TransactionRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public string Kind { get; set; } = "Payment";
    public Guid? HoldRef { get; set; }
    public decimal? CapturedAmount { get; set; }
}

public sealed class GatewayDb : DbContext
{
    public GatewayDb(DbContextOptions<GatewayDb> options) : base(options) { }

    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<GatewayPayment> Payments => Set<GatewayPayment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("gateway");

        modelBuilder.Entity<Terminal>(e =>
        {
            e.ToTable("Terminals");
            e.HasKey(t => t.TerminalId);
            e.Property(t => t.TerminalId).HasMaxLength(50);
            e.Property(t => t.BankCode).HasMaxLength(20);
            e.Property(t => t.Name).HasMaxLength(100);
            e.Property(t => t.KeyHash).HasMaxLength(64);
        });

        modelBuilder.Entity<GatewayPayment>(e =>
        {
            e.ToTable("Payments");
            e.HasKey(p => p.Id);
            e.Property(p => p.TerminalId).HasMaxLength(50);
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.Currency).HasMaxLength(3);
            e.Property(p => p.Status).HasMaxLength(20);
            e.Property(p => p.FailureReason).HasMaxLength(200);
            e.Property(p => p.Kind).HasMaxLength(10);
            e.Property(p => p.CapturedAmount).HasPrecision(18, 2);
        });
    }
}

// The gateway owns its own small schema in the same database and creates it on start. This is a
// demo shortcut: the statements are idempotent, so running them on every start is harmless, and the
// owner has no migration to run by hand.
public static class GatewaySchema
{
    public const string Sql = """
        CREATE SCHEMA IF NOT EXISTS gateway;
        CREATE TABLE IF NOT EXISTS gateway."Terminals" (
            "TerminalId" varchar(50) PRIMARY KEY,
            "BankCode" varchar(20) NOT NULL,
            "AccountId" uuid NOT NULL,
            "Name" varchar(100) NOT NULL,
            "KeyHash" varchar(64) NOT NULL,
            "CreatedAt" timestamptz NOT NULL);
        CREATE TABLE IF NOT EXISTS gateway."Payments" (
            "Id" uuid PRIMARY KEY,
            "TerminalId" varchar(50) NOT NULL,
            "Amount" numeric(18,2) NOT NULL,
            "Currency" varchar(3) NOT NULL,
            "Status" varchar(20) NOT NULL,
            "TransactionRef" uuid NULL,
            "CreatedAt" timestamptz NOT NULL,
            "ExpiresAt" timestamptz NOT NULL,
            "CompletedAt" timestamptz NULL);
        ALTER TABLE gateway."Payments" ADD COLUMN IF NOT EXISTS "FailureReason" varchar(200) NULL;
        ALTER TABLE gateway."Payments" ADD COLUMN IF NOT EXISTS "Kind" varchar(10) NOT NULL DEFAULT 'Payment';
        ALTER TABLE gateway."Payments" ADD COLUMN IF NOT EXISTS "HoldRef" uuid NULL;
        ALTER TABLE gateway."Payments" ADD COLUMN IF NOT EXISTS "CapturedAmount" numeric(18,2) NULL;
        CREATE INDEX IF NOT EXISTS "IX_Payments_Terminal_Status" ON gateway."Payments" ("TerminalId", "Status");
        CREATE TABLE IF NOT EXISTS gateway."ProcessedEvents" (
            "EventId" uuid PRIMARY KEY,
            "ReceivedAt" timestamptz NOT NULL);
        """;

    public static Task EnsureAsync(GatewayDb db) => db.Database.ExecuteSqlRawAsync(Sql);
}
