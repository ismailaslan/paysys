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

// A payment a terminal asked for.
// Payment status: Pending, Completed, Error (the bank refused it, FailureReason says why) or Expired.
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
}

// A one-use ticket a merchant's browser gets from its bank to open the live connection. Only a hash of the ticket is
// kept, and it lives 60 seconds.
public sealed class StreamTicketRow
{
    public string TicketHash { get; set; } = "";
    public string BankCode { get; set; } = "";
    public Guid[] AccountIds { get; set; } = [];
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class GatewayDb : DbContext
{
    public GatewayDb(DbContextOptions<GatewayDb> options) : base(options) { }

    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<GatewayPayment> Payments => Set<GatewayPayment>();
    public DbSet<StreamTicketRow> StreamTickets => Set<StreamTicketRow>();

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

        modelBuilder.Entity<StreamTicketRow>(e =>
        {
            e.ToTable("StreamTickets");
            e.HasKey(t => t.TicketHash);
            e.Property(t => t.TicketHash).HasMaxLength(64);
            e.Property(t => t.BankCode).HasMaxLength(20);
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
        CREATE INDEX IF NOT EXISTS "IX_Payments_Terminal_Status" ON gateway."Payments" ("TerminalId", "Status");
        CREATE TABLE IF NOT EXISTS gateway."ProcessedEvents" (
            "EventId" uuid PRIMARY KEY,
            "ReceivedAt" timestamptz NOT NULL);
        CREATE TABLE IF NOT EXISTS gateway."StreamTickets" (
            "TicketHash" varchar(64) PRIMARY KEY,
            "BankCode" varchar(20) NOT NULL,
            "AccountIds" uuid[] NOT NULL,
            "ExpiresAt" timestamptz NOT NULL);
        CREATE INDEX IF NOT EXISTS "IX_StreamTickets_ExpiresAt" ON gateway."StreamTickets" ("ExpiresAt");
        """;

    public static Task EnsureAsync(GatewayDb db) => db.Database.ExecuteSqlRawAsync(Sql);
}
