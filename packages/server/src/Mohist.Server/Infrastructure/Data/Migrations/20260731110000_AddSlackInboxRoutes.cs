using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddSlackInboxRoutes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RouteKind",
            table: "SlackProviderInboxRows",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RouteSessionId",
            table: "SlackProviderInboxRows",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RouteTurnId",
            table: "SlackProviderInboxRows",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CurrentMessageTs",
            table: "SlackDmSessionMappings",
            type: "TEXT",
            maxLength: 64,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "RouteKind", table: "SlackProviderInboxRows");
        migrationBuilder.DropColumn(name: "RouteSessionId", table: "SlackProviderInboxRows");
        migrationBuilder.DropColumn(name: "RouteTurnId", table: "SlackProviderInboxRows");
        migrationBuilder.DropColumn(name: "CurrentMessageTs", table: "SlackDmSessionMappings");
    }
}
