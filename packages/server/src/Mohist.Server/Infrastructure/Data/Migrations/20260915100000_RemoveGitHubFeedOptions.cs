using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class RemoveGitHubFeedOptions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FeedMode",
            table: "GitHubConnections");
        migrationBuilder.DropColumn(
            name: "IntakeLabel",
            table: "GitHubConnections");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FeedMode",
            table: "GitHubConnections",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "start");
        migrationBuilder.AddColumn<string>(
            name: "IntakeLabel",
            table: "GitHubConnections",
            type: "TEXT",
            maxLength: 256,
            nullable: false,
            defaultValue: "mohist");
    }
}
