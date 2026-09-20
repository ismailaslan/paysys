using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paysys.DAL.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerIdToAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every pre-existing account is assigned to the seeded "demo" user (its Id
            // comes from Auth:Users in Paysys.Api/appsettings.json) so existing data
            // stays usable. The all-zero Guid EF generates by default would match no
            // user and lock every existing account.
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "Accounts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("54887bf0-35a4-4608-b365-d2ea2b0ca58d"));

            // The default only exists to backfill; new rows must supply an owner.
            migrationBuilder.Sql("ALTER TABLE \"Accounts\" ALTER COLUMN \"OwnerId\" DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_OwnerId",
                table: "Accounts",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Accounts_OwnerId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Accounts");
        }
    }
}
