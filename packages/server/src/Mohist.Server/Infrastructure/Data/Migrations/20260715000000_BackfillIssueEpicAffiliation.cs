using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// One-time idempotent backfill of the denormalized <c>epicId</c> field in
    /// the <c>Issues.State</c> JSON column for issues already linked to an
    /// active epic at cutover. The <c>epicId</c> field was added to the
    /// <c>Issue</c> aggregate in issue #412 (T-003) so issue.* events can
    /// stamp <c>epicid</c> from their own state — without a cross-aggregate
    /// query at stamp time (D5).
    ///
    /// Live writes set <c>epicId</c> via <c>EpicGrain</c>'s synchronous
    /// push to <c>IIssueGrain.SetEpicAffiliationAsync</c> at link/unlink,
    /// which is also re-applied by the durable
    /// <c>EpicIssueLinkedHandler</c>/<c>EpicIssueUnlinkedHandler</c> on
    /// failure (self-healing drift). The backfill here covers the rows that
    /// were already linked before any of those code paths existed.
    ///
    /// The source of truth for membership is <c>EpicIssues</c> (history
    /// join) constrained by <c>EpicActiveIssues</c> (at-most-one active
    /// owner per issue). Only <c>EpicActiveIssues</c> rows are reflected
    /// on the issue, since terminal-history rows should leave the issue's
    /// denormalized cache clear (matches the live write path, which only
    /// stamps while an <see cref="Mohist.Server.Infrastructure.Data.Epic.EpicActiveIssueRow"/>
    /// exists).
    ///
    /// Idempotency: a row is updated only when <c>epicId</c> is currently
    /// absent or null (the backfill guard); rows that already carry a
    /// live-written or already-backfilled value are left alone. Only rows
    /// backed by an <c>EpicActiveIssues</c> entry are touched; rows with
    /// no active membership have their <c>epicId</c> explicitly cleared
    /// only when it was previously set by some other path (the rare edge
    /// case of a stale cache after an unlink that did not push the
    /// durable handler) — handled implicitly because the live write path
    /// is the only other setter and it always lands.
    ///
    /// No <c>BuildTargetModel</c> snapshot is provided because this
    /// migration does not modify the EF model — it is a pure data
    /// backfill. The <c>[Migration]</c> attribute is placed here (not in
    /// a Designer partial) for the same reason. The historical
    /// <c>BackfillIssueEventsTerminalTypeRename</c> and
    /// <c>BackfillIssueLegacyArrayLabels</c> migrations use the same
    /// pattern.
    /// </summary>
    [DbContext(typeof(Db.MohistDbContext))]
    [Migration("20260715000000_BackfillIssueEpicAffiliation")]
    public partial class BackfillIssueEpicAffiliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE Issues
                SET State = json_set(State, '$.epicId', (
                    SELECT a."EpicId"
                    FROM "EpicActiveIssues" a
                    WHERE a."ProjectId" = Issues."ProjectId"
                      AND a."IssueId" = Issues."IssueId"
                ))
                WHERE json_extract(State, '$.epicId') IS NULL
                  AND EXISTS (
                      SELECT 1 FROM "EpicActiveIssues" a
                      WHERE a."ProjectId" = Issues."ProjectId"
                        AND a."IssueId" = Issues."IssueId"
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: the backfill is a state projection. Stripping epicId
            // would destroy live-written values set by the EpicGrain push
            // after this migration ran. Reverting is not meaningful — the
            // backfill is intended to converge once and stay.
        }
    }
}
