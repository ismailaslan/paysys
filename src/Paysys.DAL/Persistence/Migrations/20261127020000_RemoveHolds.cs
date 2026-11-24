using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paysys.DAL.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(PaysysDbContext))]
    [Migration("20261127020000_RemoveHolds")]
    public partial class RemoveHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Reserved money was never taken from Balance (the spendable amount was Balance less HeldAmount),
            // so dropping the reservation column gives it back to the payer.
            migrationBuilder.DropTable(name: "Holds");
            migrationBuilder.DropColumn(name: "HeldAmount", table: "Accounts");
            migrationBuilder.DropColumn(name: "Hold", table: "PayeeGrants");
            migrationBuilder.DropColumn(name: "Hold", table: "PaymentCodes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "HeldAmount",
                table: "Accounts",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "Hold",
                table: "PayeeGrants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Hold",
                table: "PaymentCodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
