using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paysys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionConversion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ConvertedAmount",
                table: "Transactions",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ConvertedCurrency",
                table: "Transactions",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                table: "Transactions",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            // Existing rows were all same-currency transfers, so backfill with the
            // factually correct values (not a guess) rather than the generic 0/""
            // defaults above, then drop the defaults so future inserts must specify
            // these explicitly -- same pattern as the AccountType migration.
            migrationBuilder.Sql(
                "UPDATE \"Transactions\" SET \"ConvertedAmount\" = \"Amount\", \"ConvertedCurrency\" = \"Currency\", \"ExchangeRate\" = 1;");

            migrationBuilder.Sql("ALTER TABLE \"Transactions\" ALTER COLUMN \"ConvertedAmount\" DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE \"Transactions\" ALTER COLUMN \"ConvertedCurrency\" DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE \"Transactions\" ALTER COLUMN \"ExchangeRate\" DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConvertedAmount",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ConvertedCurrency",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                table: "Transactions");
        }
    }
}
