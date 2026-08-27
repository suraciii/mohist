using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddGitHubCommandReplyDelivery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_GitHubCommandReplies_Connection_Issue_Comment",
            table: "GitHubCommandReplies");

        migrationBuilder.AddColumn<string>(
            name: "OperationKey",
            table: "GitHubCommandReplies",
            type: "TEXT",
            maxLength: 512,
            nullable: false,
            defaultValue: "");
        migrationBuilder.AddColumn<int>(
            name: "AttemptCount",
            table: "GitHubCommandReplies",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "NextAttemptAt",
            table: "GitHubCommandReplies",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LeaseUntil",
            table: "GitHubCommandReplies",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "LastError",
            table: "GitHubCommandReplies",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "FailedAt",
            table: "GitHubCommandReplies",
            type: "TEXT",
            nullable: true);

        migrationBuilder.Sql(
            "UPDATE \"GitHubCommandReplies\" SET \"OperationKey\" = \"Marker\" WHERE \"OperationKey\" = '';");

        migrationBuilder.CreateIndex(
            name: "UX_GitHubCommandReplies_Connection_Issue_Comment_Operation",
            table: "GitHubCommandReplies",
            columns: new[] { "ConnectionId", "GithubIssueNumber", "GithubCommentId", "OperationKey" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_GitHubCommandReplies_Connection_Issue_Comment_Operation",
            table: "GitHubCommandReplies");
        migrationBuilder.DropColumn(name: "OperationKey", table: "GitHubCommandReplies");
        migrationBuilder.DropColumn(name: "AttemptCount", table: "GitHubCommandReplies");
        migrationBuilder.DropColumn(name: "NextAttemptAt", table: "GitHubCommandReplies");
        migrationBuilder.DropColumn(name: "LeaseUntil", table: "GitHubCommandReplies");
        migrationBuilder.DropColumn(name: "LastError", table: "GitHubCommandReplies");
        migrationBuilder.DropColumn(name: "FailedAt", table: "GitHubCommandReplies");
        migrationBuilder.CreateIndex(
            name: "UX_GitHubCommandReplies_Connection_Issue_Comment",
            table: "GitHubCommandReplies",
            columns: new[] { "ConnectionId", "GithubIssueNumber", "GithubCommentId" },
            unique: true);
    }
}
