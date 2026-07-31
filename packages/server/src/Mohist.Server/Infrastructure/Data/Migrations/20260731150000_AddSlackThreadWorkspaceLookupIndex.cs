using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddSlackThreadWorkspaceLookupIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_SlackThreadSessionMappings_WorkspaceTeamId_ConversationId_ThreadTs",
            table: "SlackThreadSessionMappings",
            columns: new[] { "WorkspaceTeamId", "ConversationId", "ThreadTs" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_SlackThreadSessionMappings_WorkspaceTeamId_ConversationId_ThreadTs",
            table: "SlackThreadSessionMappings");
    }
}
