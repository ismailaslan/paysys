using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paysys.DAL.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClientTypeToAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: 0 here IS the backfill: ClientType.Individual is the
            // enum's first (0) value, so every existing row lands on Individual
            // as a direct side effect of adding this non-nullable column - no
            // separate UPDATE needed, unlike AddBankIdToAccount where 0 wasn't a
            // valid value. Still dropping the default afterward so future raw
            // inserts can't rely on an implicit value, same discipline as every
            // other migration in this project.
            migrationBuilder.AddColumn<int>(
                name: "ClientType",
                table: "Accounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("ALTER TABLE \"Accounts\" ALTER COLUMN \"ClientType\" DROP DEFAULT;");

            migrationBuilder.AddColumn<string>(
                name: "TerminalId",
                table: "Accounts",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientType",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "TerminalId",
                table: "Accounts");
        }
    }
}
