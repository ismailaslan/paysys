using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paysys.DAL.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(PaysysDbContext))]
    [Migration("20261126000000_AddTotp")]
    public partial class AddTotp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Two-step sign-in. Every existing user has it off (all four columns empty).
            migrationBuilder.AddColumn<string>(
                name: "RecoveryCodeHashes",
                table: "Users",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TotpEnabledAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotpLastStep",
                table: "Users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TotpSecret",
                table: "Users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "RecoveryCodeHashes", table: "Users");
            migrationBuilder.DropColumn(name: "TotpEnabledAt", table: "Users");
            migrationBuilder.DropColumn(name: "TotpLastStep", table: "Users");
            migrationBuilder.DropColumn(name: "TotpSecret", table: "Users");
        }
    }
}
