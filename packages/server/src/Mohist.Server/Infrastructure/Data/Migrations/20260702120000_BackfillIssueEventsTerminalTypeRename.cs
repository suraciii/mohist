using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// One-time idempotent backfill of the persisted <c>IssueEvents.Type</c>
    /// column for historical terminal rows. Aligns the durable event-layer
    /// vocabulary with the renamed terminal union variants in
    /// <see cref="Mohist.Server.Issue.Domain.Events.IssueEvent"/>:
    /// <list type="bullet">
    ///   <item><description><c>com.mohist.issue.closed</c> → <c>com.mohist.issue.cancelled</c></description></item>
    ///   <item><description><c>com.mohist.issue.work-completed</c> → <c>com.mohist.issue.completed</c></description></item>
    /// </list>
    /// The Issue aggregate is state-stored (snapshot is truth and grains do
    /// not replay events on startup), so this rewrite affects only the event
    /// layer — it does NOT alter issue snapshot state, the <c>status</c>
    /// field, or the <c>IssueStatus</c> enum. Timeline rendering and terminal
    /// bucketing classify pre-rename and post-rename terminal events
    /// identically after this migration runs.
    ///
    /// Idempotency: a second <see cref="Up"/> run matches zero rows (the
    /// legacy ids are no longer present) and changes nothing. <see cref="Down"/>
    /// is symmetric — it rewrites the canonical ids back to the legacy ids,
    /// so the change is reversible in the event layer.
    ///
    /// No <c>BuildTargetModel</c> snapshot is provided because this migration
    /// does not modify the EF model — it is a pure data backfill. The
    /// <c>[Migration]</c> attribute is placed here (not in a Designer partial)
    /// for the same reason. The historical <c>BackfillIssueCompletedAt</c>
    /// migration uses the same pattern.
    /// </summary>
    [DbContext(typeof(Db.MohistDbContext))]
    [Migration("20260702120000_BackfillIssueEventsTerminalTypeRename")]
    public partial class BackfillIssueEventsTerminalTypeRename : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rewrite legacy closed → canonical cancelled. Idempotent: a
            // second run matches zero rows because the legacy id is no
            // longer present after the first run.
            migrationBuilder.Sql(
                """
                UPDATE IssueEvents
                SET Type = 'com.mohist.issue.cancelled'
                WHERE Type = 'com.mohist.issue.closed';
                """);

            // Rewrite legacy work-completed → canonical completed. Same
            // idempotency property as the rewrite above.
            migrationBuilder.Sql(
                """
                UPDATE IssueEvents
                SET Type = 'com.mohist.issue.completed'
                WHERE Type = 'com.mohist.issue.work-completed';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Symmetric reverse: rewrite the canonical terminal ids back to
            // the legacy ids. Reverses the event-layer vocabulary; does NOT
            // alter issue snapshot state, status, or the IssueStatus enum.
            migrationBuilder.Sql(
                """
                UPDATE IssueEvents
                SET Type = 'com.mohist.issue.closed'
                WHERE Type = 'com.mohist.issue.cancelled';
                """);

            migrationBuilder.Sql(
                """
                UPDATE IssueEvents
                SET Type = 'com.mohist.issue.work-completed'
                WHERE Type = 'com.mohist.issue.completed';
                """);
        }
    }
}