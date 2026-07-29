using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Materializes the new <c>WorkflowRunStatus</c> state
    /// machine on the durable schema. Two parts:
    ///
    /// <list type="number">
    ///   <item>
    ///     <description>
    ///       <b>Schema</b> — adds the <c>Status</c> STORED computed column on
    ///       <c>WorkflowRuns</c> and the <c>IX_WorkflowRuns_Status</c> index
    ///       that the two scheduling queries (<c>FindAssignableAsync</c> and
    ///       <c>FindAssignedToAsync</c>) plus the new
    ///       <c>CountRunningAssignedToAsync</c> count query rely on. The
///       DbContext model already declared both; this migration is what EF needs to materialize them on
///       disk at deploy time.
    ///       <para>
    ///         SQLite cannot <c>ALTER TABLE ADD COLUMN ... STORED</c>
    ///         directly. We use the established two-step pattern from
    ///         <c>20260629112745_AddAgentLaunchLabelComputedColumns</c>:
    ///         <c>AddColumn</c> as a nullable plain <c>TEXT</c> column first,
    ///         then <c>AlterColumn</c> to the STORED computed definition so
    ///         the provider emits its automatic table rebuild.
    ///       </para>
    ///       <para>
    ///         The computed expression is
    ///         <c>LOWER(COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status')))</c>
    ///         — the path-robustness <c>COALESCE</c> mirrors the existing
    ///         <c>MetadataProjectId</c> / <c>AgentRow.Status</c> patterns,
    ///         and the <c>LOWER</c> normalizes camelCase JSON enum values
    ///         to lowercase so the column is always the canonical form the
    ///         status-filter queries compare against.
    ///       </para>
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <b>Data reclassification (D5)</b> — every persisted workflow
    ///       run whose <c>State.status</c> still carries the old
    ///       <c>pending</c> or <c>running</c> vocabulary is rewritten in
    ///       place to its true new status using assignment + in-flight-work
    ///       facts, so existing runs land in the correct
    ///       <c>Created</c> / <c>Pending</c> / <c>Ready</c> / <c>Running</c>
    ///       bucket before the scheduler queries start filtering on the new
    ///       vocabulary. The four scenarios:
    ///       <list type="bullet">
    ///         <item><description>old <c>pending</c> (built, not started) → <c>created</c>.</description></item>
    ///         <item><description>old <c>running</c> with no <c>assignment</c> → <c>pending</c> (waiting for claim).</description></item>
    ///         <item><description>old <c>running</c> with an <c>assignment</c> and in-flight work → stays <c>running</c>.</description></item>
    ///         <item><description>old <c>running</c> with an <c>assignment</c> and no in-flight work → <c>ready</c>.</description></item>
    ///       </list>
    ///       In-flight work = any stage in <c>$.stages</c> has a task with
    ///       <c>status == "running"</c> OR a non-null <c>checksWorkId</c>.
    ///       <c>COALESCE($, $)</c> on <c>assignment.runnerId</c> /
    ///       <c>claim.runnerId</c> covers pre-rename rows whose binding
    ///       lived under the legacy <c>claim</c> field (the in-process
    ///       shim in <c>WorkflowRunStore.MigrateLegacyWorkflowRunJson</c> covers
    ///       reads, but the SQL must work against the raw state).
    ///       Terminal (<c>completed</c> / <c>failed</c> / <c>stopped</c>) and
    ///       <c>paused</c> / <c>awaitingApproval</c> rows are already
    ///       semantically correct under the new vocabulary and are left
    ///       untouched by the WHERE clauses.
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// The schema <c>Down</c> drops the <c>Status</c> column and the
    /// <c>IX_WorkflowRuns_Status</c> index. There is <b>no</b> data
    /// <c>Down</c> for the reclassification step — it is destructive on the
    /// old <c>status</c> values, and per design D5 / Migration Plan the
    /// change is forward-only (rollback strategy is restore-from-backup of
    /// the SQLite DB taken before <c>mo update</c>).
    /// </summary>
    public partial class WorkflowRunStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // -------- Schema: STORED Status computed column + index --------

            // Two-step STORED column add (see class XML doc). The plain
            // TEXT AddColumn is the SQLite-compatible scaffold; the
            // subsequent AlterColumn with computedColumnSql + stored:true
            // triggers the provider's automatic table rebuild that turns
            // it into a STORED computed column populated from State.
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "WorkflowRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "WorkflowRuns",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "LOWER(COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status')))",
                stored: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            // Composite covering index for the two scheduler queries:
            // FindAssignableAsync filters on Status alone, FindAssignedToAsync
            // filters on (Status, AssignedRunnerId). The composite matches
            // the runner-bound query exactly; the standalone Status
            // selectivity is implied by the composite's leftmost prefix.
            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Status",
                table: "WorkflowRuns",
                columns: new[] { "Status", "AssignedRunnerId" });

            // -------- Data: historical reclassification (D5) --------

            // 1) Old "pending" (built, not started) → "created". Built but
            // never Started: under the old vocabulary these sat in
            // "Pending" because there was no Created state; under D1 they
            // belong in Created.
            migrationBuilder.Sql(
                """
                UPDATE "WorkflowRuns"
                SET "State" = json_set("State", '$.status', 'created')
                WHERE LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))) = 'pending';
                """);

            // 2) Old "running" with no runner assignment → "pending".
            // These were "running" rows that never had a runner claim them
            // (e.g. stuck in the assignment pool). Under the new state
            // machine they are Pending (waiting for any runner). COALESCE
            // covers both the current `assignment` field and the legacy
            // `claim` field, so a pre-D2 row with the binding in `claim`
            // is also detected as assigned.
            migrationBuilder.Sql(
                """
                UPDATE "WorkflowRuns"
                SET "State" = json_set("State", '$.status', 'pending')
                WHERE LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))) = 'running'
                  AND json_extract("State", '$.assignment.runnerId') IS NULL
                  AND json_extract("State", '$.claim.runnerId') IS NULL;
                """);

            // 3) Old "running" with an assignment AND in-flight work stays
            // "running". In-flight = any stage in $.stages has a task
            // status "running" OR a non-null checksWorkId. The EXISTS
            // subquery against json_each limits to one row per WorkflowRuns
            // row; LOWER on the task status tolerates any PascalCase
            // historical writes.
            migrationBuilder.Sql(
                """
                UPDATE "WorkflowRuns"
                SET "State" = json_set("State", '$.status', 'running')
                WHERE LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))) = 'running'
                  AND (
                      json_extract("State", '$.assignment.runnerId') IS NOT NULL
                      OR json_extract("State", '$.claim.runnerId') IS NOT NULL
                  )
                  AND (
                      EXISTS (
                          SELECT 1
                          FROM json_each(json_extract("State", '$.stages')) AS stage
                          WHERE json_extract(stage.value, '$.checksWorkId') IS NOT NULL
                      )
                      OR EXISTS (
                          SELECT 1
                          FROM json_each(json_extract("State", '$.stages')) AS stage,
                               json_each(json_extract(stage.value, '$.tasks')) AS task
                          WHERE LOWER(COALESCE(json_extract(task.value, '$.status'), json_extract(task.value, '$.Status'))) = 'running'
                      )
                  );
                """);

            // 4) Old "running" with an assignment and NO in-flight work →
            // "ready". Allocated, dispatchable work exists, but no task is
            // currently in flight and no stage check work is outstanding —
            // exactly what the new Ready status denotes.
            migrationBuilder.Sql(
                """
                UPDATE "WorkflowRuns"
                SET "State" = json_set("State", '$.status', 'ready')
                WHERE LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))) = 'running'
                  AND (
                      json_extract("State", '$.assignment.runnerId') IS NOT NULL
                      OR json_extract("State", '$.claim.runnerId') IS NOT NULL
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM json_each(json_extract("State", '$.stages')) AS stage
                      WHERE json_extract(stage.value, '$.checksWorkId') IS NOT NULL
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM json_each(json_extract("State", '$.stages')) AS stage,
                           json_each(json_extract(stage.value, '$.tasks')) AS task
                      WHERE LOWER(COALESCE(json_extract(task.value, '$.status'), json_extract(task.value, '$.Status'))) = 'running'
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema only: drop the index and the column. There is no
            // reclassification Down — see class XML doc, design D5.
            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_Status",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "WorkflowRuns");
        }
    }
}
