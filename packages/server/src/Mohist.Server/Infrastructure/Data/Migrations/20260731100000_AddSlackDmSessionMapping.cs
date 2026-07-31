using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddSlackDmSessionMapping : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SlackDmSessionMappings",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                SlackUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                DmConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                CurrentSessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlackDmSessionMappings", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "UX_SlackDmSessionMappings_ConnectionId_DmConversationId",
            table: "SlackDmSessionMappings",
            columns: new[] { "ConnectionId", "DmConversationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SlackDmSessionMappings_ProjectId_ConnectionId_UpdatedAt",
            table: "SlackDmSessionMappings",
            columns: new[] { "ProjectId", "ConnectionId", "UpdatedAt" });

        migrationBuilder.Sql("""
            ALTER TABLE "AgentSessions"
            ADD COLUMN "LabelConnectionId" TEXT
            GENERATED ALWAYS AS (json_extract("State", '$.metadata.labels."mohist.io/connection-id"')) STORED;
            """);

        migrationBuilder.Sql("""
            ALTER TABLE "AgentSessions"
            ADD COLUMN "LabelSlackUserId" TEXT
            GENERATED ALWAYS AS (json_extract("State", '$.metadata.labels."mohist.io/slack-user-id"')) STORED;
            """);

        migrationBuilder.Sql("""
            ALTER TABLE "AgentSessions"
            ADD COLUMN "LabelSlackConversationId" TEXT
            GENERATED ALWAYS AS (json_extract("State", '$.metadata.labels."mohist.io/slack-conversation-id"')) STORED;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_AgentSessions_LabelConnectionId",
            table: "AgentSessions",
            column: "LabelConnectionId");

        migrationBuilder.CreateIndex(
            name: "IX_AgentSessions_LabelProjectId_LabelConnectionId_CreatedAt",
            table: "AgentSessions",
            columns: new[] { "LabelProjectId", "LabelConnectionId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_AgentSessions_LabelSlackUserId",
            table: "AgentSessions",
            column: "LabelSlackUserId");

        migrationBuilder.CreateIndex(
            name: "IX_AgentSessions_LabelProjectId_LabelSlackUserId_CreatedAt",
            table: "AgentSessions",
            columns: new[] { "LabelProjectId", "LabelSlackUserId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_AgentSessions_LabelSlackConversationId",
            table: "AgentSessions",
            column: "LabelSlackConversationId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_AgentSessions_LabelSlackConversationId",
            table: "AgentSessions");

        migrationBuilder.DropIndex(
            name: "IX_AgentSessions_LabelProjectId_LabelSlackUserId_CreatedAt",
            table: "AgentSessions");

        migrationBuilder.DropIndex(
            name: "IX_AgentSessions_LabelSlackUserId",
            table: "AgentSessions");

        migrationBuilder.DropIndex(
            name: "IX_AgentSessions_LabelProjectId_LabelConnectionId_CreatedAt",
            table: "AgentSessions");

        migrationBuilder.DropIndex(
            name: "IX_AgentSessions_LabelConnectionId",
            table: "AgentSessions");

        migrationBuilder.Sql("""
            ALTER TABLE "AgentSessions" DROP COLUMN "LabelSlackConversationId";
            """);
        migrationBuilder.Sql("""
            ALTER TABLE "AgentSessions" DROP COLUMN "LabelSlackUserId";
            """);
        migrationBuilder.Sql("""
            ALTER TABLE "AgentSessions" DROP COLUMN "LabelConnectionId";
            """);

        migrationBuilder.DropIndex(
            name: "IX_SlackDmSessionMappings_ProjectId_ConnectionId_UpdatedAt",
            table: "SlackDmSessionMappings");

        migrationBuilder.DropIndex(
            name: "UX_SlackDmSessionMappings_ConnectionId_DmConversationId",
            table: "SlackDmSessionMappings");

        migrationBuilder.DropTable(name: "SlackDmSessionMappings");
    }
}
