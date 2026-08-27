using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddGitHubOperationRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "NeedsReprojection",
            table: "GitHubConnections",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "Kind",
            table: "GitHubIssueCommentOperations",
            type: "TEXT",
            maxLength: 32,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "Body",
            table: "GitHubIssueCommentOperations",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "StateReason",
            table: "GitHubIssueCommentOperations",
            type: "TEXT",
            maxLength: 32,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "Marker",
            table: "GitHubIssueCommentOperations",
            type: "TEXT",
            maxLength: 512,
            nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "AttemptCount",
            table: "GitHubIssueCommentOperations",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "NextAttemptAt",
            table: "GitHubIssueCommentOperations",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LeaseUntil",
            table: "GitHubIssueCommentOperations",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "LastError",
            table: "GitHubIssueCommentOperations",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "FailedAt",
            table: "GitHubIssueCommentOperations",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "NeedsReprojection", table: "GitHubConnections");
        migrationBuilder.DropColumn(name: "Kind", table: "GitHubIssueCommentOperations");
        migrationBuilder.DropColumn(name: "Body", table: "GitHubIssueCommentOperations");
        migrationBuilder.DropColumn(name: "StateReason", table: "GitHubIssueCommentOperations");
        migrationBuilder.DropColumn(name: "Marker", table: "GitHubIssueCommentOperations");
        migrationBuilder.DropColumn(name: "AttemptCount", table: "GitHubIssueCommentOperations");
        migrationBuilder.DropColumn(name: "NextAttemptAt", table: "GitHubIssueCommentOperations");
        migrationBuilder.DropColumn(name: "LeaseUntil", table: "GitHubIssueCommentOperations");
        migrationBuilder.DropColumn(name: "LastError", table: "GitHubIssueCommentOperations");
        migrationBuilder.DropColumn(name: "FailedAt", table: "GitHubIssueCommentOperations");
    }
}
