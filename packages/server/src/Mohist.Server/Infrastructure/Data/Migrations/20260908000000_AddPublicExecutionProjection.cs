using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Adds the durable public execution projection: one snapshot row per
/// public read anchor (AgentJob / Session Input / Session Turn), the
/// per-Session public event journal with its stream generations and
/// replay-deduplication transition identity, the per-Session stream
/// state (active generation, global next-sequence allocator, retained
/// floor, safe head, closed tombstone), and the per-feed source
/// checkpoints that prove which durable canonical facts the projection
/// has consumed. The tables are new; no existing table changes. They
/// are inert until the public execution projector writes them, so the
/// rollback is a plain drop.
/// </summary>
public partial class AddPublicExecutionProjection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "public_execution_snapshots",
            columns: table => new
            {
                AnchorType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                AnchorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                AgentId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                SessionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                TerminalFact = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                TerminalOutcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                TerminalAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                TerminalSequence = table.Column<long>(type: "INTEGER", nullable: true),
                LastSequence = table.Column<long>(type: "INTEGER", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_public_execution_snapshots", x => new { x.AnchorType, x.AnchorId });
            });

        migrationBuilder.CreateTable(
            name: "public_session_events",
            columns: table => new
            {
                SessionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Generation = table.Column<long>(type: "INTEGER", nullable: false),
                Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                Type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                OccurredAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                SourceTransition = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                RecordedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_public_session_events", x => new { x.SessionId, x.Generation, x.Sequence });
            });

        migrationBuilder.CreateTable(
            name: "public_stream_states",
            columns: table => new
            {
                SessionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ActiveGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                NextSequence = table.Column<long>(type: "INTEGER", nullable: false),
                EarliestSequence = table.Column<long>(type: "INTEGER", nullable: true),
                LatestSequence = table.Column<long>(type: "INTEGER", nullable: true),
                Closed = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_public_stream_states", x => x.SessionId);
            });

        migrationBuilder.CreateTable(
            name: "public_projection_checkpoints",
            columns: table => new
            {
                Feed = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                SourceKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Watermark = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_public_projection_checkpoints", x => new { x.Feed, x.SourceKey });
            });

        migrationBuilder.CreateIndex(
            name: "IX_public_execution_snapshots_SessionId",
            table: "public_execution_snapshots",
            column: "SessionId");

        migrationBuilder.CreateIndex(
            name: "IX_public_execution_snapshots_SessionId_LastSequence",
            table: "public_execution_snapshots",
            columns: new[] { "SessionId", "LastSequence" });

        migrationBuilder.CreateIndex(
            name: "UX_public_session_events_Transition",
            table: "public_session_events",
            columns: new[] { "SessionId", "Generation", "SourceTransition" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_public_session_events_SessionId_Generation_Sequence",
            table: "public_session_events",
            columns: new[] { "SessionId", "Generation", "Sequence" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "public_execution_snapshots");

        migrationBuilder.DropTable(
            name: "public_session_events");

        migrationBuilder.DropTable(
            name: "public_stream_states");

        migrationBuilder.DropTable(
            name: "public_projection_checkpoints");
    }
}
