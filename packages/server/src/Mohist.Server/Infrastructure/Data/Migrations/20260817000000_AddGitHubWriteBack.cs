using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Progress write-back: the link gains its projected state label, the
/// connection gains the NeedsAttention flag (401/403 credential problems),
/// and failed write-back operations are recorded in a dedicated table.
/// </summary>
[DbContext(typeof(MohistDbContext))]
[Migration("20260817000000_AddGitHubWriteBack")]
public partial class AddGitHubWriteBack : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "StateLabel",
            table: "GitHubIssueLinks",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "NeedsAttention",
            table: "GitHubConnections",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "GitHubWriteBackFailures",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                RepositoryName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                GithubIssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                EventType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Operation = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ErrorDetail = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GitHubWriteBackFailures", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_GitHubWriteBackFailures_ProjectId_CreatedAt",
            table: "GitHubWriteBackFailures",
            columns: new[] { "ProjectId", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "GitHubWriteBackFailures");

        // StateLabel / NeedsAttention are not reverted: this project is
        // still in active development, so column drops on SQLite are
        // forward-only (see the repo's existing migrations).
    }
}
