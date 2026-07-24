using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddIssueWatchEntries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WatchEntries",
            columns: table => new
            {
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                AgentId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WatchEntries", x => new { x.ProjectId, x.IssueNumber, x.AgentId });
            });

        migrationBuilder.CreateIndex(
            name: "UX_WatchEntries_ProjectId_IssueNumber_AgentId",
            table: "WatchEntries",
            columns: new[] { "ProjectId", "IssueNumber", "AgentId" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_WatchEntries_ProjectId_IssueNumber",
            table: "WatchEntries",
            columns: new[] { "ProjectId", "IssueNumber" });
        migrationBuilder.CreateIndex(
            name: "IX_WatchEntries_ProjectId_IssueNumber_State",
            table: "WatchEntries",
            columns: new[] { "ProjectId", "IssueNumber", "State" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "WatchEntries");
}
