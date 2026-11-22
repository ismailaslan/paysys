using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paysys.DAL.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(PaysysDbContext))]
    [Migration("20261123100000_AddCardStatus")]
    public partial class AddCardStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing cards are all active (0).
            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "CardTokens",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                table: "CardTokens");
        }
    }
}
