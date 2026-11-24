using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paysys.DAL.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(PaysysDbContext))]
    [Migration("20261125000000_AddHolds")]
    public partial class AddHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing accounts have nothing reserved.
            migrationBuilder.AddColumn<decimal>(
                name: "HeldAmount",
                table: "Accounts",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "Holds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CapturedDestinationAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    CardTokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DestinationAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DestinationCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PayerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Holds", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Holds_DestinationAccountId",
                table: "Holds",
                column: "DestinationAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Holds_IdempotencyKey",
                table: "Holds",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Holds_PayerAccountId",
                table: "Holds",
                column: "PayerAccountId");

            // The safety net: whatever code path forgets about holds, the database refuses to reserve more than the
            // balance, or a negative reservation. This is not in the EF model on purpose; it only ever fails loudly.
            migrationBuilder.Sql(
                "ALTER TABLE \"Accounts\" ADD CONSTRAINT \"CK_Accounts_HeldAmount_WithinBalance\" CHECK (\"HeldAmount\" >= 0 AND \"HeldAmount\" <= \"Balance\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"Accounts\" DROP CONSTRAINT IF EXISTS \"CK_Accounts_HeldAmount_WithinBalance\";");
            migrationBuilder.DropTable(name: "Holds");
            migrationBuilder.DropColumn(name: "HeldAmount", table: "Accounts");
        }
    }
}
