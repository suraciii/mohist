using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddGitHubMirrorIntent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "MirrorCreateAttempted",
            table: "GitHubIssueLinks",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
        migrationBuilder.AddColumn<string>(
            name: "MirrorMarker",
            table: "GitHubIssueLinks",
            type: "TEXT",
            maxLength: 256,
            nullable: true);
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_GitHubIssueLinks_ProjectId_RepositoryName_GithubIssueNumber\"");
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_GitHubIssueLinks_ProjectId_IssueNumber\"");
        migrationBuilder.Sql("DELETE FROM GitHubIssueLinks WHERE Id IN (SELECT newer.Id FROM GitHubIssueLinks newer JOIN GitHubIssueLinks older ON older.ProjectId = newer.ProjectId AND older.IssueNumber = newer.IssueNumber AND (older.CreatedAt < newer.CreatedAt OR (older.CreatedAt = newer.CreatedAt AND older.Id < newer.Id)))");
        migrationBuilder.CreateIndex(
            name: "IX_GitHubIssueLinks_ProjectId_RepositoryName_GithubIssueNumber",
            table: "GitHubIssueLinks",
            columns: new[] { "ProjectId", "RepositoryName", "GithubIssueNumber" },
            unique: true,
            filter: "\"GithubIssueNumber\" > 0");
        migrationBuilder.CreateIndex(
            name: "IX_GitHubIssueLinks_ProjectId_IssueNumber",
            table: "GitHubIssueLinks",
            columns: new[] { "ProjectId", "IssueNumber" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_GitHubIssueLinks_ProjectId_IssueNumber\"");
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_GitHubIssueLinks_ProjectId_RepositoryName_GithubIssueNumber\"");
        migrationBuilder.CreateIndex(
            name: "IX_GitHubIssueLinks_ProjectId_RepositoryName_GithubIssueNumber",
            table: "GitHubIssueLinks",
            columns: new[] { "ProjectId", "RepositoryName", "GithubIssueNumber" },
            unique: true);
        migrationBuilder.DropColumn(name: "MirrorCreateAttempted", table: "GitHubIssueLinks");
        migrationBuilder.DropColumn(name: "MirrorMarker", table: "GitHubIssueLinks");
    }
}
