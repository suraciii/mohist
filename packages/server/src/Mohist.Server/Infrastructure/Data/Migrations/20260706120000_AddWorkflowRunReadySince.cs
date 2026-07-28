using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Epic #44 (scheduling convergence): adds the <c>ReadySince</c> fairness
    /// ordering key as a VIRTUAL (non-stored) computed column on
    /// <c>WorkflowRuns</c>, plus the <c>IX_WorkflowRuns_Status_ReadySince</c>
    /// covering index that backs the round-robin scheduler query
    /// (<c>FindAssignedToAsync</c>: <c>WHERE Status='ready' AND
    /// AssignedRunnerId=@runner ORDER BY ReadySince ASC</c>).
    /// <para>
    /// <c>ReadySince</c> is the moment the run last (re-)entered Ready; the
    /// scheduler serves Ready runs oldest-first so a just-served run re-queues
    /// at the tail with zero scheduler state. VIRTUAL because the
    /// column is read only to ORDER BY, never filtered on — no need to pay the
    /// storage/write cost of a STORED column.
    /// </para>
    /// <para>
    /// Single-step <c>AddColumn</c> with <c>computedColumnSql</c> +
    /// <c>stored:false</c>, mirroring
    /// <c>20260622130000_WorkflowAssignmentPullScheduling</c>
    /// (<c>AssignedRunnerId</c> / <c>CreatedAt</c>). The two-step
    /// <c>AddColumn</c> + <c>AlterColumn</c> pattern does not work for VIRTUAL
    /// columns on the SQLite provider (the <c>AlterColumn</c> table rebuild
    /// drops the generated expression).
    /// </para>
    /// <para>
    /// Existing Ready rows backfill <c>readySince</c> on <c>State</c> from
    /// <c>assignment.assignedAt</c> (assignment is the first Ready entry, so
    /// it is the best available proxy for "when did this run become
    /// dispatchable"). Non-Ready rows are left null.
    /// </para>
    /// </summary>
    [DbContext(typeof(Db.MohistDbContext))]
    [Migration("20260706120000_AddWorkflowRunReadySince")]
    public partial class AddWorkflowRunReadySince : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // -------- Schema: VIRTUAL ReadySince computed column + index --------

            // Single-step VIRTUAL column add, mirroring
            // 20260622130000_WorkflowAssignmentPullScheduling (AssignedRunnerId /
            // CreatedAt): AddColumn with computedColumnSql + stored:false. EF
            // tracks the column as database-generated so the change tracker
            // never writes it on INSERT/UPDATE. (The two-step AddColumn +
            // AlterColumn pattern does NOT work for VIRTUAL columns on the
            // SQLite provider — the AlterColumn table rebuild drops the
            // generated expression.)
            migrationBuilder.AddColumn<DateTime>(
                name: "ReadySince",
                table: "WorkflowRuns",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "COALESCE(json_extract(State, '$.readySince'), json_extract(State, '$.ReadySince'))",
                stored: false);

            // Covering index for the fairness query: filter on
            // (Status, AssignedRunnerId) then order by ReadySince.
            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Status_ReadySince",
                table: "WorkflowRuns",
                columns: new[] { "Status", "AssignedRunnerId", "ReadySince" });

            // -------- Data: backfill ReadySince on existing Ready rows --------

            // Stored into State so the computed column picks it up on read.
            // assignment.assignedAt is the first Ready entry; rows that never
            // reached Ready stay null (the ordering key is only consulted for
            // Ready rows).
            migrationBuilder.Sql(
                """
                UPDATE "WorkflowRuns"
                SET "State" = json_set("State", '$.readySince', json_extract("State", '$.assignment.assignedAt'))
                WHERE LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))) = 'ready'
                  AND json_extract("State", '$.readySince') IS NULL
                  AND json_extract("State", '$.assignment.assignedAt') IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_Status_ReadySince",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "ReadySince",
                table: "WorkflowRuns");
        }
    }
}
