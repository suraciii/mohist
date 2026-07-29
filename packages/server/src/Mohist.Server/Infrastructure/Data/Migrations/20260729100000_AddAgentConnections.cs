using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    public partial class AddAgentConnections : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentConnections",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProviderKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    BotUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    BotName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AvatarHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SetupProgress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DesiredState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ConnectionHealth = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    HealthReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    AgentReadiness = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OwnerSlackUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentConnections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_AgentConnections_ProjectId_AgentId_WorkspaceTeamId",
                table: "AgentConnections",
                columns: new[] { "ProjectId", "AgentId", "WorkspaceTeamId" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentConnections_ProjectId_AgentId",
                table: "AgentConnections",
                columns: new[] { "ProjectId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentConnections_Id",
                table: "AgentConnections",
                column: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AgentConnections");
        }
    }
}