using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddVerifiedBotIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "VerifiedBotName",
            table: "AgentConnections",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "VerifiedBotIconUrl",
            table: "AgentConnections",
            type: "TEXT",
            maxLength: 2048,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "VerifiedBotName",
            table: "AgentConnections");

        migrationBuilder.DropColumn(
            name: "VerifiedBotIconUrl",
            table: "AgentConnections");
    }
}
