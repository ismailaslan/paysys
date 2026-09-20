using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paysys.DAL.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddActorUserIdToAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActorUserId",
                table: "TransactionAuditLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransactionAuditLogs_ActorUserId",
                table: "TransactionAuditLogs",
                column: "ActorUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TransactionAuditLogs_ActorUserId",
                table: "TransactionAuditLogs");

            migrationBuilder.DropColumn(
                name: "ActorUserId",
                table: "TransactionAuditLogs");
        }
    }
}
