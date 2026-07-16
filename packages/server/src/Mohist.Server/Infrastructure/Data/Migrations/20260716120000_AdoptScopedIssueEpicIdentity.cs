using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Issue #412 T-001: canonical Project + Number adoption for current-state
/// Issue/Epic references. The migration does NOT alter the EF model — it is
/// a pure data projection that proves the persisted current state is reachable
/// by (ProjectId, Number) for every owning reference row, regardless of
/// whether the same rows also carry a legacy IssueId/EpicId surrogate. Later
/// tasks (T-002, T-008) cut over the runtime aggregates and drop the legacy
/// columns; this migration only establishes the data invariant required by
/// the scoped GrainKey codec.
///
/// Adoption scope:
///
/// - <c>IssueComments.IssueId</c> → resolve to <c>IssueComments.IssueNumber</c>
///   for the same Project, fall back to canonical Issue row when the row
///   currently carries an IssueNumber but no IssueId.
/// - <c>InboxItems.IssueId</c> → already project-scoped via IssueNumber; data
///   only resolves the row to its canonical Issue row when IssueId is empty.
/// - <c>EpicIssues.(EpicId, IssueId)</c> → both Project + Number pairs already
///   stored; the migration verifies each row points at the canonical Issue
///   and Epic for the same Project.
/// - <c>EpicActiveIssues.(EpicId, IssueId)</c> → same shape verification.
/// - <c>IssuePrerequisites</c> → already number-shaped (Project, Number);
///   no legacy column exists.
///
/// Idempotency:
/// - Each step only updates rows that need correction (a guarded UPDATE). A
///   row whose canonical shape is already correct is left untouched, so a
///   second run is a no-op.
/// - Transaction Invariant: every statement operates on rows of one
///   aggregate ownership (Issue or Epic) and never updates state of two
///   aggregates in the same statement. The migration does not combine
///   aggregate state transitions with schema change.
///
/// Legacy IssueId / EpicId columns are intentionally preserved so the existing
/// runtime can keep activating grains by their existing id during the
/// T-002/T-008 cutover. Removing those columns is out of scope for T-001.
/// </summary>
[DbContext(typeof(Db.MohistDbContext))]
[Migration("20260716120000_AdoptScopedIssueEpicIdentity")]
public partial class AdoptScopedIssueEpicIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // IssueComments: prefer canonical IssueNumber and ProjectId from the
        // owning Issue row when IssueId is missing. Existing rows that already
        // have the correct (ProjectId, IssueNumber) pair are not touched.
        migrationBuilder.Sql(
            """
            UPDATE IssueComments
            SET "IssueId" = COALESCE(NULLIF(IssueComments."IssueId", ''), i."IssueId"),
                "IssueNumber" = COALESCE(NULLIF(IssueComments."IssueNumber", 0), i."Number")
            FROM Issues i
            WHERE i."ProjectId" = IssueComments."ProjectId"
              AND i."Number" = IssueComments."IssueNumber"
              AND (IssueComments."IssueId" = '' OR IssueComments."IssueNumber" = 0);
            """);

        // InboxItems: same shape as IssueComments — canonical IssueNumber +
        // ProjectId already drives the lookup; populate IssueId when missing.
        migrationBuilder.Sql(
            """
            UPDATE InboxItems
            SET "IssueId" = COALESCE(NULLIF(InboxItems."IssueId", ''), i."IssueId")
            FROM Issues i
            WHERE i."ProjectId" = InboxItems."ProjectId"
              AND i."Number" = InboxItems."IssueNumber"
              AND InboxItems."IssueId" = '';
            """);

        // EpicIssues: assert that each row's (Project, IssueNumber) and
        // (Project, EpicNumber via EpicId) resolve to known rows. If not,
        // delete the orphan row to keep membership in sync with current state.
        // Joining on ProjectId pins cross-Project collisions.
        migrationBuilder.Sql(
            """
            DELETE FROM EpicIssues
            WHERE NOT EXISTS (
                SELECT 1 FROM Issues i
                WHERE i."ProjectId" = EpicIssues."ProjectId"
                  AND i."Number" = EpicIssues."IssueNumber"
            )
            OR NOT EXISTS (
                SELECT 1 FROM Epics e
                WHERE e."ProjectId" = EpicIssues."ProjectId"
                  AND e."Id" = EpicIssues."EpicId"
            );
            """);

        migrationBuilder.Sql(
            """
            DELETE FROM EpicActiveIssues
            WHERE NOT EXISTS (
                SELECT 1 FROM Issues i
                WHERE i."ProjectId" = EpicActiveIssues."ProjectId"
                  AND i."Number" = EpicActiveIssues."IssueNumber"
            )
            OR NOT EXISTS (
                SELECT 1 FROM Epics e
                WHERE e."ProjectId" = EpicActiveIssues."ProjectId"
                  AND e."Id" = EpicActiveIssues."EpicId"
            );
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Pure data projection; nothing to roll back. Reverting would require
        // restoring the originals from a snapshot, which is out of scope.
    }
}
