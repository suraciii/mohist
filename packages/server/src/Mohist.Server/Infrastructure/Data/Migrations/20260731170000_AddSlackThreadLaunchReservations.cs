using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddSlackThreadLaunchReservations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SlackThreadLaunchReservations",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ThreadTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                LaunchMessageTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                SlackUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                SessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlackThreadLaunchReservations", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "UX_SlackThreadLaunchReservations_ConnectionId_WorkspaceTeamId_ConversationId_ThreadTs",
            table: "SlackThreadLaunchReservations",
            columns: new[] { "ConnectionId", "WorkspaceTeamId", "ConversationId", "ThreadTs" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SlackThreadLaunchReservations_ProjectId_ConnectionId_UpdatedAt",
            table: "SlackThreadLaunchReservations",
            columns: new[] { "ProjectId", "ConnectionId", "UpdatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "SlackThreadLaunchReservations");
}
