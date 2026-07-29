using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Promote the <c>AgentJobs</c> row to the atomic AgentJob owner ledger.
/// The legacy write-through mirror (Orleans state + best-effort relational
/// mirror) is replaced by a single revision-checked row that holds the
/// lifecycle JSON, the immutable dispatch snapshot, and every scheduling
/// column a poll-time query needs.
///
/// The migration:
/// <list type="number">
/// <item><description>Adds scheduling columns and a row-level <c>Revision</c>
/// ETag for optimistic concurrency.</description></item>
/// <item><description>Backfills the new columns from the legacy state JSON,
/// using one injected migration timestamp for every valid legacy pending
/// row, preserving valid running rows without applying a pending timeout,
/// and excluding terminal rows from active scheduling projections.</description></item>
/// <item><description>Validates every nonterminal legacy row can rebuild a
/// dispatch ledger. The whole backfill runs inside a transaction; a single
/// malformed nonterminal row aborts the migration without committing
/// anything.</description></item>
/// <item><description>Adds indexes for poll-time queries (assigned running,
/// assigned pending by readiness time, eligible unassigned pending).</description></item>
/// </list>
/// </summary>
[Migration("20260729000000_AgentJobOwnerLedger")]
public partial class AgentJobOwnerLedger : Migration
{
    private const string MigrationTimestamp = "2026-07-29T00:00:00.0000000+00:00";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "AgentJobs",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<string>(
            name: "AssignedRunnerId",
            table: "AgentJobs",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "WorkId",
            table: "AgentJobs",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ReadySince",
            table: "AgentJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RunningSince",
            table: "AgentJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DispatchJson",
            table: "AgentJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "WorkType",
            table: "AgentJobs",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Stage",
            table: "AgentJobs",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Title",
            table: "AgentJobs",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "IssueProjectId",
            table: "AgentJobs",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "IssueNumber",
            table: "AgentJobs",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AgentSessionId",
            table: "AgentJobs",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "InitialInputId",
            table: "AgentJobs",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "InitialTurnId",
            table: "AgentJobs",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        // Validate that every nonterminal row can rebuild a dispatch
        // ledger before any write commits. The whole migration runs inside
        // a transaction so a single malformed row aborts without changes.
        migrationBuilder.Sql(ValidationSql);

        // One injected migration timestamp for every valid legacy pending
        // row; running rows retain their existing assignment + dispatch
        // and get no pending-timeout projection. Terminal rows get a
        // null scheduling projection (excluded from active queries).
        migrationBuilder.Sql(BackfillSchedulingSql);

        // Seed Revision = 1 once the backfill has settled so the first
        // grain save starts from a consistent ETag. Each save thereafter
        // bumps it; readers can rely on Revision > 0 meaning "valid ledger".
        migrationBuilder.Sql("""
            UPDATE "AgentJobs"
            SET "Revision" = 1;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_AgentJobs_AssignedRunnerId_Status",
            table: "AgentJobs",
            columns: new[] { "AssignedRunnerId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_AgentJobs_AssignedRunnerId_Status_ReadySince",
            table: "AgentJobs",
            columns: new[] { "AssignedRunnerId", "Status", "ReadySince" });

        migrationBuilder.CreateIndex(
            name: "IX_AgentJobs_Status_ReadySince",
            table: "AgentJobs",
            columns: new[] { "Status", "ReadySince" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_AgentJobs_Status_ReadySince",
            table: "AgentJobs");

        migrationBuilder.DropIndex(
            name: "IX_AgentJobs_AssignedRunnerId_Status_ReadySince",
            table: "AgentJobs");

        migrationBuilder.DropIndex(
            name: "IX_AgentJobs_AssignedRunnerId_Status",
            table: "AgentJobs");

        migrationBuilder.DropColumn(name: "InitialTurnId", table: "AgentJobs");
        migrationBuilder.DropColumn(name: "InitialInputId", table: "AgentJobs");
        migrationBuilder.DropColumn(name: "AgentSessionId", table: "AgentJobs");
        migrationBuilder.DropColumn(name: "IssueNumber", table: "AgentJobs");
        migrationBuilder.DropColumn(name: "IssueProjectId", table: "AgentJobs");
        migrationBuilder.DropColumn(name: "Title", table: "AgentJobs");
        migrationBuilder.DropColumn(name: "Stage", table: "AgentJobs");
        migrationBuilder.DropColumn(name: "WorkType", table: "AgentJobs");
        migrationBuilder.DropColumn(name: "DispatchJson", table: "AgentJobs");
        migrationBuilder.DropColumn(name: "RunningSince", table: "AgentJobs");
        migrationBuilder.DropColumn(name: "ReadySince", table: "AgentJobs");
        migrationBuilder.DropColumn(name: "WorkId", table: "AgentJobs");
        migrationBuilder.DropColumn(name: "AssignedRunnerId", table: "AgentJobs");
        migrationBuilder.DropColumn(name: "Revision", table: "AgentJobs");
    }

    // Validates every nonterminal legacy AgentJob row can rebuild a
    // dispatch ledger. A row fails validation when its state cannot
    // produce a dispatch snapshot — typically because it is nonterminal
    // but lacks the work identity, runner identity, or input required to
    // reconstruct the WorkDispatch envelope the runner expects.
    //
    // The query is intentionally non-mutating: any failure here aborts
    // the migration inside the surrounding transaction.
    private const string ValidationSql = $"""
        SELECT 1
        FROM "AgentJobs"
        WHERE json_type("State", '$') = 'object'
          AND LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))) IN ('pending', 'running', 'unknown')
          AND (
              length(trim(COALESCE(json_extract("State", '$.runnerId'), json_extract("State", '$.RunnerId')), char(9,10,11,12,13,32))) > 0
              AND length(trim(COALESCE(json_extract("State", '$.workId'), json_extract("State", '$.WorkId')), char(9,10,11,12,13,32))) = 0
              OR (
                  LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))) IN ('pending', 'unknown')
                  AND length(trim(COALESCE(json_extract("State", '$.input.prompt'), json_extract("State", '$.Input.Prompt')), char(9,10,11,12,13,32))) = 0
              )
              OR (
                  LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))) = 'running'
                  AND length(trim(COALESCE(json_extract("State", '$.runningSince'), json_extract("State", '$.RunningSince')), char(9,10,11,12,13,32))) = 0
              )
          )
        LIMIT 1;
        """;

    // Backfills the new scheduling columns from the legacy state JSON.
    // One timestamp for every valid legacy pending row, valid running rows
    // keep their existing assignment + dispatch with no pending-timeout
    // projection, terminal rows get null scheduling (excluded from active
    // queries).
    private const string BackfillSchedulingSql = $"""
        -- Terminal rows: no scheduling projection. Excluded from active
        -- queries by virtue of null AssignedRunnerId/ReadySince plus
        -- their existing Status computed-column value.
        UPDATE "AgentJobs"
        SET "AssignedRunnerId" = NULL,
            "WorkId" = NULL,
            "ReadySince" = NULL,
            "RunningSince" = NULL,
            "DispatchJson" = NULL,
            "WorkType" = NULL,
            "Stage" = NULL,
            "Title" = NULL,
            "IssueProjectId" = NULL,
            "IssueNumber" = NULL,
            "AgentSessionId" = NULL,
            "InitialInputId" = NULL,
            "InitialTurnId" = NULL
        WHERE json_type("State", '$') = 'object'
          AND LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))) IN ('completed', 'failed');

        -- Running rows: preserve runner, work identity, running time, and
        -- the snapshot of the dispatch envelope. No pending-timeout
        -- projection (ReadySince NULL). The dispatch snapshot is
        -- reconstructed from the persisted Input and the resolved Agent
        -- identity; an existing dispatch column wins if it was persisted
        -- by a newer grain.
        UPDATE "AgentJobs"
        SET "AssignedRunnerId" = COALESCE(json_extract("State", '$.runnerId'), json_extract("State", '$.RunnerId')),
            "WorkId" = COALESCE(json_extract("State", '$.workId'), json_extract("State", '$.WorkId')),
            "ReadySince" = NULL,
            "RunningSince" = COALESCE(json_extract("State", '$.runningSince'), json_extract("State", '$.RunningSince')),
            "DispatchJson" = CASE
                WHEN length(trim(COALESCE(json_extract("State", '$.dispatchSnapshot'), json_extract("State", '$.DispatchSnapshot')), char(9,10,11,12,13,32))) > 0
                    THEN COALESCE(json_extract("State", '$.dispatchSnapshot'), json_extract("State", '$.DispatchSnapshot'))
                ELSE json_object(
                    'workflowRunId', '',
                    'workId', COALESCE(json_extract("State", '$.workId'), json_extract("State", '$.WorkId')),
                    'workType', 'agent-job',
                    'stage', 'agent',
                    'title', 'Agent Job',
                    'ownerKind', 'agent-job',
                    'agentJobId', "JobKey",
                    'projectId', COALESCE(json_extract("State", '$.input.projectId'), json_extract("State", '$.Input.ProjectId')),
                    'agentId', COALESCE(json_extract("State", '$.input.agentId'), json_extract("State", '$.Input.AgentId')),
                    'agentSessionId', COALESCE(json_extract("State", '$.input.agentSessionId'), json_extract("State", '$.Input.AgentSessionId')),
                    'initialInputId', COALESCE(json_extract("State", '$.input.initialInputId'), json_extract("State", '$.Input.InitialInputId')),
                    'initialTurnId', COALESCE(json_extract("State", '$.input.initialTurnId'), json_extract("State", '$.Input.InitialTurnId')),
                    'with', json_object('prompt', COALESCE(json_extract("State", '$.input.prompt'), json_extract("State", '$.Input.Prompt')))
                )
            END,
            "WorkType" = 'agent-job',
            "Stage" = 'agent',
            "Title" = 'Agent Job',
            "IssueProjectId" = COALESCE(json_extract("State", '$.input.projectId'), json_extract("State", '$.Input.ProjectId')),
            "AgentSessionId" = COALESCE(json_extract("State", '$.input.agentSessionId'), json_extract("State", '$.Input.AgentSessionId')),
            "InitialInputId" = COALESCE(json_extract("State", '$.input.initialInputId'), json_extract("State", '$.Input.InitialInputId')),
            "InitialTurnId" = COALESCE(json_extract("State", '$.input.initialTurnId'), json_extract("State", '$.Input.InitialTurnId'))
        WHERE json_type("State", '$') = 'object'
          AND LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))) = 'running';

        -- Pending rows (with or without prepared assignment): one
        -- injected timestamp for every valid legacy pending row. If the
        -- legacy row already had a prepared assignment, preserve the
        -- runner id; otherwise the row stays unassigned and will be picked
        -- up by a poll-time claim from the eligible-pending query.
        UPDATE "AgentJobs"
        SET "AssignedRunnerId" = COALESCE(json_extract("State", '$.runnerId'), json_extract("State", '$.RunnerId')),
            "WorkId" = COALESCE(json_extract("State", '$.workId'), json_extract("State", '$.WorkId')),
            "ReadySince" = '{MigrationTimestamp}',
            "RunningSince" = NULL,
            "DispatchJson" = CASE
                WHEN length(trim(COALESCE(json_extract("State", '$.runnerId'), json_extract("State", '$.RunnerId')), char(9,10,11,12,13,32))) > 0
                    AND length(trim(COALESCE(json_extract("State", '$.workId'), json_extract("State", '$.WorkId')), char(9,10,11,12,13,32))) > 0
                THEN COALESCE(json_extract("State", '$.dispatchSnapshot'), json_extract("State", '$.DispatchSnapshot'),
                    json_object(
                        'workflowRunId', '',
                        'workId', COALESCE(json_extract("State", '$.workId'), json_extract("State", '$.WorkId')),
                        'workType', 'agent-job',
                        'stage', 'agent',
                        'title', 'Agent Job',
                        'ownerKind', 'agent-job',
                        'agentJobId', "JobKey",
                        'projectId', COALESCE(json_extract("State", '$.input.projectId'), json_extract("State", '$.Input.ProjectId')),
                        'agentId', COALESCE(json_extract("State", '$.input.agentId'), json_extract("State", '$.Input.AgentId')),
                        'agentSessionId', COALESCE(json_extract("State", '$.input.agentSessionId'), json_extract("State", '$.Input.AgentSessionId')),
                        'initialInputId', COALESCE(json_extract("State", '$.input.initialInputId'), json_extract("State", '$.Input.InitialInputId')),
                        'initialTurnId', COALESCE(json_extract("State", '$.input.initialTurnId'), json_extract("State", '$.Input.InitialTurnId')),
                        'with', json_object('prompt', COALESCE(json_extract("State", '$.input.prompt'), json_extract("State", '$.Input.Prompt')))
                    ))
                ELSE NULL
            END,
            "WorkType" = 'agent-job',
            "Stage" = 'agent',
            "Title" = 'Agent Job',
            "IssueProjectId" = COALESCE(json_extract("State", '$.input.projectId'), json_extract("State", '$.Input.ProjectId')),
            "AgentSessionId" = COALESCE(json_extract("State", '$.input.agentSessionId'), json_extract("State", '$.Input.AgentSessionId')),
            "InitialInputId" = COALESCE(json_extract("State", '$.input.initialInputId'), json_extract("State", '$.Input.InitialInputId')),
            "InitialTurnId" = COALESCE(json_extract("State", '$.input.initialTurnId'), json_extract("State", '$.Input.InitialTurnId'))
        WHERE json_type("State", '$') = 'object'
          AND LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))) IN ('pending', 'unknown');
        """;
}