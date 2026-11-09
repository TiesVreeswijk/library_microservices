using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MemberService.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberStatusAndDeletionSaga : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CardNumber",
                table: "Members",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            // Edited by hand: EF generated defaultValue "", which is not a valid MemberStatus.
            // Members that already exist are normal, active members.
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Members",
                type: "text",
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.CreateTable(
                name: "DeletionSagas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    CurrentStep = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeletionSagas", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeletionSagas_MemberId",
                table: "DeletionSagas",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_DeletionSagas_State",
                table: "DeletionSagas",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeletionSagas");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Members");

            migrationBuilder.AlterColumn<string>(
                name: "CardNumber",
                table: "Members",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
