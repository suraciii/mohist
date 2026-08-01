using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddAccessPolicyAndAllowedMembers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AccessPolicy",
            table: "AgentConnections",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "owner_only");

        migrationBuilder.Sql("""
            UPDATE "AgentConnections" SET "AccessPolicy" = 'owner_only' WHERE "AccessPolicy" IS NULL OR "AccessPolicy" = '';
            """);

        migrationBuilder.CreateTable(
            name: "SlackConnectionAllowedMembers",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                SlackUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlackConnectionAllowedMembers", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "UX_SlackConnectionAllowedMembers_ProjectId_ConnectionId_SlackUserId",
            table: "SlackConnectionAllowedMembers",
            columns: new[] { "ProjectId", "ConnectionId", "SlackUserId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SlackConnectionAllowedMembers_ProjectId_ConnectionId",
            table: "SlackConnectionAllowedMembers",
            columns: new[] { "ProjectId", "ConnectionId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SlackConnectionAllowedMembers");
        migrationBuilder.DropColumn(name: "AccessPolicy", table: "AgentConnections");
    }
}
