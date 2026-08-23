using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Adds the provider-independent, durable receipt used by Agent Session
/// retries. The table is additive: no existing table or row is rewritten.
/// </summary>
public partial class AddAgentRetryOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "agent_retry_operations",
            columns: table => new
            {
                OperationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                SessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                TurnId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                PreAllocatedSessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                PreAllocatedInputId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                PreAllocatedTurnId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                ResultState = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                ResultText = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                ResultJobKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                ResultSessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                ResultInputId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                ResultTurnId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_retry_operations", x => x.OperationId);
            });

        migrationBuilder.CreateIndex(
            name: "UX_agent_retry_operations_IdempotencyKey",
            table: "agent_retry_operations",
            column: "IdempotencyKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "UX_agent_retry_operations_SessionId_TurnId",
            table: "agent_retry_operations",
            columns: new[] { "SessionId", "TurnId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "agent_retry_operations");
}
