using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    public partial class AddSlackProviderInboxOutbox : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SlackProviderInboxRows",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SlackMessageIdentity = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DmConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SlackUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackProviderInboxRows", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_SlackProviderInboxRows_ConnectionId_SlackMessageIdentity",
                table: "SlackProviderInboxRows",
                columns: new[] { "ConnectionId", "SlackMessageIdentity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlackProviderInboxRows_ProjectId_ConnectionId_DispatchedAt",
                table: "SlackProviderInboxRows",
                columns: new[] { "ProjectId", "ConnectionId", "DispatchedAt" });

            migrationBuilder.CreateTable(
                name: "SlackOutboxRows",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DmConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DispatchRef = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ClaimedByAdapterId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeliveryUncertainAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeadLetteredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackOutboxRows", x => x.Id);
                    table.CheckConstraint(
                        "CK_SlackOutboxRows_Kind",
                        "\"Kind\" IN ('replaceable_progress', 'terminal_result', 'explicit_failure', 'user_action')");
                    table.CheckConstraint(
                        "CK_SlackOutboxRows_State",
                        "\"State\" IN ('pending', 'claimed', 'delivered', 'delivery_uncertain', 'dead_lettered')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SlackOutboxRows_ProjectId_ConnectionId_State",
                table: "SlackOutboxRows",
                columns: new[] { "ProjectId", "ConnectionId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_SlackOutboxRows_ConnectionId_State_NextAttemptAt",
                table: "SlackOutboxRows",
                columns: new[] { "ConnectionId", "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SlackOutboxRows_ConnectionId_State_ClaimedAt",
                table: "SlackOutboxRows",
                columns: new[] { "ConnectionId", "State", "ClaimedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SlackOutboxRows_ConnectionId_State_DeliveryUncertainAt",
                table: "SlackOutboxRows",
                columns: new[] { "ConnectionId", "State", "DeliveryUncertainAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SlackOutboxRows_ConnectionId_DispatchRef_Kind_State",
                table: "SlackOutboxRows",
                columns: new[] { "ConnectionId", "DispatchRef", "Kind", "State" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SlackOutboxRows");
            migrationBuilder.DropTable(name: "SlackProviderInboxRows");
        }
    }
}
