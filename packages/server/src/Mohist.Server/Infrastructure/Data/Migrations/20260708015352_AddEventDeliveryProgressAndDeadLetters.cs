using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventDeliveryProgressAndDeadLetters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DispatchedAt",
                table: "WorkflowRunEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DispatchedAt",
                table: "IssueEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DispatchedAt",
                table: "EpicEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeadLetters",
                columns: table => new
                {
                    DeadLetterId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DataContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "JSON", nullable: false),
                    ExtensionsJson = table.Column<string>(type: "JSON", nullable: false),
                    FailingHandler = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorStack = table.Column<string>(type: "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DeadLetteredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadLetters", x => x.DeadLetterId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRunEvents_Type_Time",
                table: "WorkflowRunEvents",
                columns: new[] { "Type", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRunEvents_Undelivered",
                table: "WorkflowRunEvents",
                columns: new[] { "Source", "Id" },
                filter: "\"DispatchedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IssueEvents_Type_Time",
                table: "IssueEvents",
                columns: new[] { "Type", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_IssueEvents_Undelivered",
                table: "IssueEvents",
                columns: new[] { "Source", "Id" },
                filter: "\"DispatchedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EpicEvents_Undelivered",
                table: "EpicEvents",
                columns: new[] { "Source", "Id" },
                filter: "\"DispatchedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetters_DeadLetteredAt",
                table: "DeadLetters",
                column: "DeadLetteredAt");

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetters_FailingHandler_DeadLetteredAt",
                table: "DeadLetters",
                columns: new[] { "FailingHandler", "DeadLetteredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowRunEvents_Type_Time",
                table: "WorkflowRunEvents");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRunEvents_Undelivered",
                table: "WorkflowRunEvents");

            migrationBuilder.DropIndex(
                name: "IX_IssueEvents_Type_Time",
                table: "IssueEvents");

            migrationBuilder.DropIndex(
                name: "IX_IssueEvents_Undelivered",
                table: "IssueEvents");

            migrationBuilder.DropIndex(
                name: "IX_EpicEvents_Undelivered",
                table: "EpicEvents");

            migrationBuilder.DropTable(
                name: "DeadLetters");

            migrationBuilder.DropColumn(
                name: "DispatchedAt",
                table: "WorkflowRunEvents");

            migrationBuilder.DropColumn(
                name: "DispatchedAt",
                table: "IssueEvents");

            migrationBuilder.DropColumn(
                name: "DispatchedAt",
                table: "EpicEvents");
        }
    }
}
