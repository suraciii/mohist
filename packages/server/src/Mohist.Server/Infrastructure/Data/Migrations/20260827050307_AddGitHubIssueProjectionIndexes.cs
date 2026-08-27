using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddGitHubIssueProjectionIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_GitHubIssueLinks_ProjectId_IssueNumber",
            table: "GitHubIssueLinks",
            columns: new[] { "ProjectId", "IssueNumber" });

        migrationBuilder.CreateIndex(
            name: "IX_GitHubConnections_ProjectId_RepositoryName",
            table: "GitHubConnections",
            columns: new[] { "ProjectId", "RepositoryName" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_GitHubIssueLinks_ProjectId_IssueNumber",
            table: "GitHubIssueLinks");

        migrationBuilder.DropIndex(
            name: "IX_GitHubConnections_ProjectId_RepositoryName",
            table: "GitHubConnections");
    }
}
