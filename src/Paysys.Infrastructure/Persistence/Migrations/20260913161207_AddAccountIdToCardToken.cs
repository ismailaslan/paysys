using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paysys.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountIdToCardToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                table: "CardTokens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill the 2 surviving CardToken rows with their real owning account,
            // inferred from Transaction.CardTokenId history (see conversation notes):
            // - 2466ef36...: used by transactions from both Alice AND Bob (proof of the
            //   pre-fix bug this migration exists to close) - assigned to Alice, the
            //   account it was originally tokenized for.
            // - 8a8ea796...: used only by a transaction from Alice - unambiguous.
            migrationBuilder.Sql(
                "UPDATE \"CardTokens\" SET \"AccountId\" = '8668ce4a-153c-413b-930d-859386f4ac2c' " +
                "WHERE \"Id\" IN ('2466ef36-5926-4b59-96a5-2446126acbd0', '8a8ea796-20cc-4f7b-9431-7b66ec8f2a9d');");

            migrationBuilder.Sql("ALTER TABLE \"CardTokens\" ALTER COLUMN \"AccountId\" DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "CardTokens");
        }
    }
}
