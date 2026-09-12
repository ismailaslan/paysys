using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paysys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccountType",
                table: "Accounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing rows are backfilled to 0 (Person) by the AddColumn default above.
            // Drop the default afterward so the column has no default going forward --
            // every future insert (via EF or otherwise) must specify AccountType explicitly.
            migrationBuilder.Sql("ALTER TABLE \"Accounts\" ALTER COLUMN \"AccountType\" DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccountType",
                table: "Accounts");
        }
    }
}
