using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260909000000_AddSlackRetryOperations")]
public partial class AddSlackRetryOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SlackRetryOperations",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ActionKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                SessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                FailedInputId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                FailedTurnId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                DispatchRef = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                MessageTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ThreadTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                OriginalDirectMessage = table.Column<bool>(type: "INTEGER", nullable: false),
                ActorSlackUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                RetryDispatchKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                AttemptKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                PreMintedSessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                PreMintedInputId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                PreMintedTurnId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                FollowupOperationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                ResultSessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                ResultInputId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                ResultTurnId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                ResultReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                RecoveryLeaseId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                RecoveryLeaseExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlackRetryOperations", x => x.Id);
                table.CheckConstraint(
                    "CK_SlackRetryOperations_State",
                    "\"State\" IN ('dispatch-pending', 'completed')");
            });

        migrationBuilder.CreateIndex(
            name: "UX_SlackRetryOperations_ProjectId_ActionKey",
            table: "SlackRetryOperations",
            columns: new[] { "ProjectId", "ActionKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SlackRetryOperations_State_RecoveryLeaseExpiresAt",
            table: "SlackRetryOperations",
            columns: new[] { "State", "RecoveryLeaseExpiresAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "SlackRetryOperations");
}
