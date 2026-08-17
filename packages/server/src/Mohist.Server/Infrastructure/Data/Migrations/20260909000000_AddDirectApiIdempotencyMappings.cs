using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Adds the durable direct API request fence for launch, follow-up, and stop.
/// The table is additive; pending rows intentionally survive process loss so
/// a retry can re-enter the canonical idempotent operation.
/// </summary>
public partial class AddDirectApiIdempotencyMappings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "direct_api_idempotency_mappings",
            columns: table => new
            {
                Command = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                ScopeKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                CallerKeyId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Outcome = table.Column<string>(type: "TEXT", nullable: true),
                FrozenTarget = table.Column<string>(type: "TEXT", nullable: true),
                TurnId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_direct_api_idempotency_mappings", x => new { x.Command, x.ScopeKey });
            });

        migrationBuilder.CreateIndex(
            name: "UX_direct_api_idempotency_mappings_Command_ScopeKey",
            table: "direct_api_idempotency_mappings",
            columns: new[] { "Command", "ScopeKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "UX_direct_api_idempotency_mappings_PendingStop_TurnId",
            table: "direct_api_idempotency_mappings",
            column: "TurnId",
            unique: true,
            filter: "\"Command\" = 'stop' AND \"State\" IN ('pending')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "direct_api_idempotency_mappings");
    }
}
