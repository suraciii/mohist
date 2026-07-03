using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskLogTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskLogBatches",
                columns: table => new
                {
                    OwnerKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Truncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskLogBatches", x => new { x.OwnerKind, x.OwnerId, x.WorkId });
                });

            migrationBuilder.CreateTable(
                name: "TaskLogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskLogEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskLogEntries_Owner_WorkId_Seq",
                table: "TaskLogEntries",
                columns: new[] { "OwnerKind", "OwnerId", "WorkId", "Seq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskLogBatches");

            migrationBuilder.DropTable(
                name: "TaskLogEntries");
        }
    }
}
