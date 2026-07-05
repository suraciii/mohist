using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// One-time backfill of the missing <c>com.mohist.issue.completed</c>
    /// CloudEvent into <see cref="Db.MohistDbContext.IssueEvents"/> for every
    /// <c>done</c> issue that reached the terminal state while the append path
    /// was broken (<c>IssueGrain.SaveIssueAsync</c> snapshotted
    /// <c>PendingEvents</c> by reference, so <c>ClearPendingEvents()</c>
    /// drained it before <c>PublishIssueEventsAsync</c> could publish — every
    /// issue lifecycle CloudEvent recorded before that fix was lost).
    ///
    /// Each row mirrors exactly what <see cref="Events.IssueEventSerializer.BusType"/>
    /// would have emitted: <c>Type = 'com.mohist.issue.completed'</c>,
    /// <c>Source = '/mohist/issues/' || IssueId</c>, <c>Subject = issue.Number</c>,
    /// <c>Data = {"workflowRunId": ...}</c>, <c>ExtensionsJson</c> carrying
    /// projectid/issueid/issueno. <c>Id</c> is per-source 1-based (the table
    /// holds no prior row for these sources). <c>cancelled</c> issues are
    /// intentionally out of scope — throughput measures delivery, not failure
    /// cadence, so only <c>completed</c> is reconstructed.
    ///
    /// Time: prefer <c>completedAt</c> from the JSON snapshot; fall back to
    /// <c>updatedAt</c> when <c>completedAt</c> is absent (legacy snapshots
    /// from before the field existed). Gated on the absence of an existing
    /// <c>com.mohist.issue.completed</c> row per source for idempotency.
    ///
    /// Down is a no-op: the rows are additive history. Deleting them would
    /// discard the only record of these terminal transitions.
    ///
    /// No <c>BuildTargetModel</c> snapshot — pure data backfill, no model
    /// change. The <c>[Migration]</c> attribute is inline for the same reason
    /// as <c>BackfillIssueCompletedAt</c>.
    /// </summary>
    [DbContext(typeof(Db.MohistDbContext))]
    [Migration("20260705132535_BackfillIssueCompletedEvents")]
    public partial class BackfillIssueCompletedEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Insert one synthesized `com.mohist.issue.completed` row per
            // done issue lacking one. Time = completedAt when present,
            // otherwise updatedAt. Id is 1 (no prior row exists for these
            // sources — the broken append left them empty). Status is matched
            // case-insensitively to cover both legacy ('Done') and current
            // ('done') snapshot spellings.
            migrationBuilder.Sql(
                """
                INSERT INTO IssueEvents (
                    Id, Source, EventId, Type, Time, SpecVersion,
                    Subject, DataContentType, Data, ExtensionsJson
                )
                SELECT
                    1 AS Id,
                    '/mohist/issues/' || i.IssueId AS Source,
                    lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2))
                          || '-' || hex(randomblob(2)) || '-' || hex(randomblob(6))) AS EventId,
                    'com.mohist.issue.completed' AS Type,
                    COALESCE(
                        json_extract(i.State, '$.completedAt'),
                        json_extract(i.State, '$.updatedAt')
                    ) AS Time,
                    '1.0' AS SpecVersion,
                    CAST(CAST(json_extract(i.State, '$.number') AS INTEGER) AS TEXT) AS Subject,
                    'application/json' AS DataContentType,
                    json_object(
                        'workflowRunId',
                        json_extract(i.State, '$.workflowRunId')
                    ) AS Data,
                    json_object(
                        'projectid', json_extract(i.State, '$.projectId'),
                        'issueid',   i.IssueId,
                        'issueno',   CAST(CAST(json_extract(i.State, '$.number') AS INTEGER) AS TEXT)
                    ) AS ExtensionsJson
                FROM Issues i
                WHERE LOWER(json_extract(i.State, '$.status')) = 'done'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM IssueEvents e
                      WHERE e.Source = '/mohist/issues/' || i.IssueId
                        AND e.Type = 'com.mohist.issue.completed'
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: the rows are additive history. Removing them would lose
            // the only record of these terminal transitions.
        }
    }
}
