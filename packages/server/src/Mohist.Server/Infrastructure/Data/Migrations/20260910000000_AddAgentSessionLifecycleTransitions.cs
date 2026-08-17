using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Adds the durable canonical Session lifecycle history consumed by the
/// public execution projector. The current AgentSession ledger remains the
/// mutable aggregate snapshot; this table preserves public-relevant status
/// transitions that can occur between projector sweeps.
/// </summary>
public partial class AddAgentSessionLifecycleTransitions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AgentSessionLifecycleTransitions",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                SourceTransition = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                AnchorKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                AnchorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AgentSessionLifecycleTransitions", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AgentSessionLifecycleTransitions_SessionId_Id",
            table: "AgentSessionLifecycleTransitions",
            columns: new[] { "SessionId", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AgentSessionLifecycleTransitions");
    }
}
