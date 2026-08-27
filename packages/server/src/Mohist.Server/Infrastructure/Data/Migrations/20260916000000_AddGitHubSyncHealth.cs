using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddGitHubSyncHealth : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SyncStatus",
            table: "GitHubIssueLinks",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "healthy");
        migrationBuilder.AddColumn<string>(
            name: "LastErrorOperation",
            table: "GitHubIssueLinks",
            type: "TEXT",
            maxLength: 64,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "LastErrorCode",
            table: "GitHubIssueLinks",
            type: "TEXT",
            maxLength: 64,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "LastErrorDetail",
            table: "GitHubIssueLinks",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LastErrorAt",
            table: "GitHubIssueLinks",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SyncStatus", table: "GitHubIssueLinks");
        migrationBuilder.DropColumn(name: "LastErrorOperation", table: "GitHubIssueLinks");
        migrationBuilder.DropColumn(name: "LastErrorCode", table: "GitHubIssueLinks");
        migrationBuilder.DropColumn(name: "LastErrorDetail", table: "GitHubIssueLinks");
        migrationBuilder.DropColumn(name: "LastErrorAt", table: "GitHubIssueLinks");
    }
}
