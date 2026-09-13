using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paysys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FlagReason",
                table: "Transactions");

            migrationBuilder.CreateTable(
                name: "TransactionAuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TimestampLocal = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActionType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    FromAccountRef = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ToAccountRef = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CardTokenRef = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TerminalId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FlagReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PreviousHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionAuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransactionAuditLogs_TransactionAuditLogs_PreviousEntryId",
                        column: x => x.PreviousEntryId,
                        principalTable: "TransactionAuditLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransactionAuditLogs_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionAuditLogs_PreviousEntryId",
                table: "TransactionAuditLogs",
                column: "PreviousEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransactionAuditLogs_TransactionId",
                table: "TransactionAuditLogs",
                column: "TransactionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TransactionAuditLogs");

            migrationBuilder.AddColumn<string>(
                name: "FlagReason",
                table: "Transactions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }
    }
}
