using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Issue-179: relax the unique constraint on
    /// <c>IX_EpicIssues_ProjectId_IssueId</c> to a non-unique index.
    ///
    /// The "at most one non-terminal epic membership per issue" invariant is
    /// enforced in application code (<c>EpicGrain.LinkIssueAsync</c>) so a
    /// terminal-epic membership and a new non-terminal membership can
    /// coexist for the same issue — this is required for re-homing an
    /// issue out of a finished (done/closed) epic into a new active one.
    /// See design.md D2 for the rationale and the rejected alternatives
    /// (denormalized OwnerTerminal flag + partial unique index).
    /// </summary>
    public partial class DropEpicIssueMembershipUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EpicIssues_ProjectId_IssueId",
                table: "EpicIssues");

            migrationBuilder.CreateIndex(
                name: "IX_EpicIssues_ProjectId_IssueId",
                table: "EpicIssues",
                columns: new[] { "ProjectId", "IssueId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EpicIssues_ProjectId_IssueId",
                table: "EpicIssues");

            migrationBuilder.CreateIndex(
                name: "IX_EpicIssues_ProjectId_IssueId",
                table: "EpicIssues",
                columns: new[] { "ProjectId", "IssueId" },
                unique: true);
        }
    }
}
