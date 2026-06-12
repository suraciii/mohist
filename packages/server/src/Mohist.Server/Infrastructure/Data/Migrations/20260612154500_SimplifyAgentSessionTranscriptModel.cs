using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    public partial class SimplifyAgentSessionTranscriptModel : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE AgentSessions
                SET State = json_set(
                    State,
                    '$.Metadata.Labels."mohist.io/project-id"',
                    COALESCE(json_extract(State, '$.Metadata.Labels."mohist.io/project-id"'), ProjectId),
                    '$.Metadata.Labels."mohist.io/issue-number"',
                    COALESCE(json_extract(State, '$.Metadata.Labels."mohist.io/issue-number"'), CAST(IssueNumber AS TEXT)),
                    '$.Metadata.Labels."mohist.io/source-kind"',
                    COALESCE(json_extract(State, '$.Metadata.Labels."mohist.io/source-kind"'), 'workflow'),
                    '$.Metadata.Labels."mohist.io/source-id"',
                    COALESCE(json_extract(State, '$.Metadata.Labels."mohist.io/source-id"'), WorkflowRunId),
                    '$.Metadata.Labels."mohist.io/session-name"',
                    COALESCE(json_extract(State, '$.Metadata.Labels."mohist.io/session-name"'), SessionName)
                );
                """);
            migrationBuilder.Sql(
                """
                UPDATE AgentSessions
                SET State = json_set(State, '$.Metadata.Labels."mohist.io/work-id"', COALESCE(json_extract(State, '$.Metadata.Labels."mohist.io/work-id"'), WorkId))
                WHERE WorkId IS NOT NULL;
                """);
            migrationBuilder.Sql(
                """
                UPDATE AgentSessions
                SET State = json_set(State, '$.Metadata.Labels."mohist.io/work-type"', COALESCE(json_extract(State, '$.Metadata.Labels."mohist.io/work-type"'), WorkType))
                WHERE WorkType IS NOT NULL;
                """);
            migrationBuilder.Sql(
                """
                UPDATE AgentSessions
                SET State = json_set(State, '$.Metadata.Labels."mohist.io/stage"', COALESCE(json_extract(State, '$.Metadata.Labels."mohist.io/stage"'), Stage))
                WHERE Stage IS NOT NULL;
                """);

            RebuildAgentSessions(migrationBuilder);
            CreateAgentSessionLabels(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "IX_AgentSessionTranscriptTurns_WorkflowRunId_SessionName_Sequence",
                table: "AgentSessionTranscriptTurns");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessionTranscriptParts_SessionId_Sequence",
                table: "AgentSessionTranscriptParts");

            migrationBuilder.DropColumn(name: "AgentSessionId", table: "AgentSessionTranscriptTurns");
            migrationBuilder.DropColumn(name: "IssueNumber", table: "AgentSessionTranscriptTurns");
            migrationBuilder.DropColumn(name: "ProjectId", table: "AgentSessionTranscriptTurns");
            migrationBuilder.DropColumn(name: "SessionName", table: "AgentSessionTranscriptTurns");
            migrationBuilder.DropColumn(name: "WorkflowRunId", table: "AgentSessionTranscriptTurns");

            migrationBuilder.DropColumn(name: "AgentSessionId", table: "AgentSessionTranscriptParts");
            migrationBuilder.DropColumn(name: "IssueNumber", table: "AgentSessionTranscriptParts");
            migrationBuilder.DropColumn(name: "ProjectId", table: "AgentSessionTranscriptParts");
            migrationBuilder.DropColumn(name: "SessionId", table: "AgentSessionTranscriptParts");
            migrationBuilder.DropColumn(name: "SessionName", table: "AgentSessionTranscriptParts");
            migrationBuilder.DropColumn(name: "Stage", table: "AgentSessionTranscriptParts");
            migrationBuilder.DropColumn(name: "WorkId", table: "AgentSessionTranscriptParts");
            migrationBuilder.DropColumn(name: "WorkType", table: "AgentSessionTranscriptParts");
            migrationBuilder.DropColumn(name: "WorkflowRunId", table: "AgentSessionTranscriptParts");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // This project is still in active development; migrations are forward-only.
        }

        private static void RebuildAgentSessions(MigrationBuilder migrationBuilder)
        {
            DropAgentSessionIndexes(migrationBuilder);
            migrationBuilder.RenameTable(name: "AgentSessions", newName: "AgentSessions_old_projection");

            migrationBuilder.CreateTable(
                name: "AgentSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    RunnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AgentSessionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastDataAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_AgentSessions", x => x.Id));

            migrationBuilder.Sql(
                """
                INSERT INTO AgentSessions (Id, State, RunnerId, AgentSessionId, Status, CreatedAt, LastDataAt, CompletedAt, UpdatedAt)
                SELECT Id, State, RunnerId, AgentSessionId, Status, CreatedAt, LastDataAt, CompletedAt, UpdatedAt
                FROM AgentSessions_old_projection;
                """);
            migrationBuilder.DropTable(name: "AgentSessions_old_projection");
            CreateAgentSessionIndexes(migrationBuilder);
        }

        private static void DropAgentSessionIndexes(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_AgentSessions_AgentSessionId";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_AgentSessions_ProjectId_IssueNumber_CreatedAt";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_AgentSessions_ProjectId_Status_CreatedAt";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_AgentSessions_WorkflowRunId_SessionName";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_AgentSessions_WorkflowRunId_WorkId";""");
        }

        private static void CreateAgentSessionIndexes(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_AgentSessionId",
                table: "AgentSessions",
                column: "AgentSessionId");
            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_Status_CreatedAt",
                table: "AgentSessions",
                columns: new[] { "Status", "CreatedAt" });
        }

        private static void CreateAgentSessionLabels(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentSessionLabels",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_AgentSessionLabels", x => new { x.SessionId, x.Key }));

            foreach (var key in new[]
            {
                "mohist.io/project-id",
                "mohist.io/issue-number",
                "mohist.io/source-kind",
                "mohist.io/source-id",
                "mohist.io/session-name",
                "mohist.io/work-id",
                "mohist.io/work-type",
                "mohist.io/stage",
            })
            {
                migrationBuilder.Sql($$"""
                    INSERT OR REPLACE INTO AgentSessionLabels (SessionId, Key, Value)
                    SELECT Id, '{{key}}', json_extract(State, '$.Metadata.Labels."{{key}}"')
                    FROM AgentSessions
                    WHERE json_extract(State, '$.Metadata.Labels."{{key}}"') IS NOT NULL
                      AND json_extract(State, '$.Metadata.Labels."{{key}}"') <> '';
                    """);
            }

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionLabels_Key_Value_SessionId",
                table: "AgentSessionLabels",
                columns: new[] { "Key", "Value", "SessionId" });
        }
    }
}
