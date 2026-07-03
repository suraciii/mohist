using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// One-time idempotent backfill of the persisted <c>Issues.State</c>
    /// column for legacy rows whose <c>labels</c> field still carries the
    /// pre-#149 flat <c>string[]</c> shape. The Issue aggregate's labels moved
    /// from a flat array (<c>["bug","runner"]</c>) to a single-value key-value
    /// map (<c>{"kind":"bug"}</c>) in #149 (commit cac0feb5). #149 intentionally
    /// shipped no data migration — it relied on a runtime tolerance in
    /// <c>IssueStore.NormalizeLegacyLabels</c> that silently discarded legacy
    /// array-form labels and substituted an empty map. That tolerance was
    /// removed in a later IssueStore refactor (commit b018dcdc), which left the
    /// legacy rows un-deserializable: <c>System.Text.Json</c> refuses to read a
    /// JSON array into <c>Dictionary&lt;string,string&gt;</c>, breaking every
    /// list/query API.
    ///
    /// This migration aligns the persisted state with the post-#149 schema the
    /// same way the lost runtime tolerance did: legacy array-form labels are
    /// rewritten to an empty object (<c>{}</c>). Per the #149 non-goal, the
    /// legacy flat tags carry no key-value structure and are not migrated into
    /// structured keys — they are discarded. This is the same posture #149
    /// chose ("silently discarding legacy flat labels, which is permitted by
    /// the non-goal"); this migration merely persists what the runtime used to
    /// paper over.
    ///
    /// Idempotency: a second <see cref="Up"/> run matches zero rows (no row's
    /// <c>labels</c> is an array anymore) and changes nothing. Only rows whose
    /// <c>labels</c> is a JSON array are touched; rows whose <c>labels</c> is
    /// already an object (the post-#149 shape) are left untouched, as are rows
    /// where <c>labels</c> is absent. <see cref="Down"/> is intentionally a
    /// no-op: the legacy flat tags are gone after Up, so a reverse migration
    /// cannot restore them — reverting would only resurrect an
    /// un-deserializable shape.
    ///
    /// No <c>BuildTargetModel</c> snapshot is provided because this migration
    /// does not modify the EF model — it is a pure data backfill. The
    /// <c>[Migration]</c> attribute is placed here (not in a Designer partial)
    /// for the same reason. The historical
    /// <c>BackfillIssueEventsTerminalTypeRename</c> migration uses the same
    /// pattern.
    /// </summary>
    [DbContext(typeof(Db.MohistDbContext))]
    [Migration("20260703140000_BackfillIssueLegacyArrayLabels")]
    public partial class BackfillIssueLegacyArrayLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rewrite legacy array-form labels to an empty object. Uses SQLite
            // JSON functions: json_type(...)='array' scopes the rewrite to
            // legacy rows only, and json_set rewrites just the labels field
            // while preserving every other field in the State JSON (id,
            // number, status, risk, completedAt, workflow refs, ...). Idempotent:
            // a second run matches zero rows because no labels is an array
            // after the first run.
            migrationBuilder.Sql(
                """
                UPDATE Issues
                SET State = json_set(State, '$.labels', json('{}'))
                WHERE json_type(State, '$.labels') = 'array';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally a no-op. Up discards legacy flat tags (per the #149
            // non-goal) and rewrites their carrier to an empty object; the
            // discarded tags cannot be reconstructed, so a reverse migration
            // has nothing faithful to restore. Reverting would only resurrect
            // the un-deserializable array shape, so we deliberately do not.
        }
    }
}
