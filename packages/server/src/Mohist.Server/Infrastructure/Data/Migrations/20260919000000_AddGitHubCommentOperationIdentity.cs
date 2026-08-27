using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddGitHubCommentOperationIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "GithubIssueNumber",
            table: "GitHubIssueCommentOperations",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.Sql("""
            UPDATE "GitHubIssueCommentOperations"
            SET "GithubIssueNumber" = COALESCE(
                (SELECT "GithubIssueNumber"
                 FROM "GitHubIssueLinks"
                 WHERE "GitHubIssueLinks"."Id" = "GitHubIssueCommentOperations"."LinkId"),
                0)
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "GithubIssueNumber",
            table: "GitHubIssueCommentOperations");
    }
}
