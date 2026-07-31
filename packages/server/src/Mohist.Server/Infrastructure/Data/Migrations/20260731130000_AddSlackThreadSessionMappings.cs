using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddSlackThreadSessionMappings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SlackThreadSessionMappings",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ThreadTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                SlackUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                SessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                RootMessageTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlackThreadSessionMappings", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "UX_SlackThreadSessionMappings_ConnectionId_WorkspaceTeamId_ConversationId_ThreadTs",
            table: "SlackThreadSessionMappings",
            columns: new[] { "ConnectionId", "WorkspaceTeamId", "ConversationId", "ThreadTs" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SlackThreadSessionMappings_ProjectId_WorkspaceTeamId_ConversationId_ThreadTs",
            table: "SlackThreadSessionMappings",
            columns: new[] { "ProjectId", "WorkspaceTeamId", "ConversationId", "ThreadTs" });

        migrationBuilder.CreateIndex(
            name: "IX_SlackThreadSessionMappings_ProjectId_ConnectionId_UpdatedAt",
            table: "SlackThreadSessionMappings",
            columns: new[] { "ProjectId", "ConnectionId", "UpdatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SlackThreadSessionMappings");
    }
}