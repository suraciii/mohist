using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260810000000_AddGitHubIssueLinks")]
public partial class AddGitHubIssueLinks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "GitHubIssueLinks",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                RepositoryName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                GithubIssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                PostedCommentsJson = table.Column<string>(type: "JSON", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GitHubIssueLinks", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_GitHubIssueLinks_ProjectId_RepositoryName_GithubIssueNumber",
            table: "GitHubIssueLinks",
            columns: new[] { "ProjectId", "RepositoryName", "GithubIssueNumber" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "GitHubIssueLinks");
    }
}
