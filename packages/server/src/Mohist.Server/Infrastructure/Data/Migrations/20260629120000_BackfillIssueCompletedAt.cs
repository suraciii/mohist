using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// One-time idempotent backfill of <c>completedAt</c> in the issue JSON
    /// snapshot for issues already in a terminal state (<c>done</c> or
    /// <c>cancelled</c>). Derives each value from <c>IssueEvents</c>:
    /// <list type="bullet">
    ///   <item><description><c>done</c> issues take <c>MAX(Time)</c> of <c>com.mohist.issue.work-completed</c> events.</description></item>
    ///   <item><description><c>cancelled</c> issues take <c>MAX(Time)</c> of <c>com.mohist.issue.closed</c> events.</description></item>
    /// </list>
    /// Keyed by <c>Source = '/mohist/issues/' || IssueId</c>, gated on
    /// <c>completedAt IS NULL</c> for idempotency.
    ///
    /// Down is a no-op — the field is additive; removing it would lose
    /// live-written values.
    ///
    /// No <c>BuildTargetModel</c> snapshot is provided because this migration
    /// does not modify the EF model — it is a pure data backfill. The
    /// <c>[Migration]</c> attribute is placed here (not in a Designer partial)
    /// for the same reason.
    /// </summary>
    [DbContext(typeof(Db.MohistDbContext))]
    [Migration("20260629120000_BackfillIssueCompletedAt")]
    public partial class BackfillIssueCompletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill completedAt for done issues from the most recent
            // work-completed event time. Gated on completedAt IS NULL so
            // already-backfilled or live-written values are preserved.
            migrationBuilder.Sql(
                """
                UPDATE Issues
                SET State = json_set(State, '$.completedAt', (
                    SELECT MAX(e.Time) FROM IssueEvents e
                    WHERE e.Source = '/mohist/issues/' || Issues.IssueId
                      AND e.Type = 'com.mohist.issue.work-completed'
                ))
                WHERE json_extract(State, '$.completedAt') IS NULL
                  AND COALESCE(json_extract(State,'$.status'), json_extract(State,'$.Status')) = 'done';
                """);

            // Backfill completedAt for cancelled issues from the most recent
            // closed event time. Same idempotency guard.
            migrationBuilder.Sql(
                """
                UPDATE Issues
                SET State = json_set(State, '$.completedAt', (
                    SELECT MAX(e.Time) FROM IssueEvents e
                    WHERE e.Source = '/mohist/issues/' || Issues.IssueId
                      AND e.Type = 'com.mohist.issue.closed'
                ))
                WHERE json_extract(State, '$.completedAt') IS NULL
                  AND COALESCE(json_extract(State,'$.status'), json_extract(State,'$.Status')) = 'cancelled';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: completedAt is additive. Stripping it would destroy
            // live-written values set after the migration ran.
        }
    }
}
