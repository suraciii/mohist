using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class HardenDeadLetterRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RedeliveryAttemptedAt",
                table: "DeadLetters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolvedAt",
                table: "DeadLetters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "DeadLetters",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetters_Source_Id_FailingHandler",
                table: "DeadLetters",
                columns: new[] { "Source", "Id", "FailingHandler" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DeadLetters_Source_Id_FailingHandler",
                table: "DeadLetters");

            migrationBuilder.DropColumn(
                name: "RedeliveryAttemptedAt",
                table: "DeadLetters");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "DeadLetters");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "DeadLetters");
        }
    }
}
