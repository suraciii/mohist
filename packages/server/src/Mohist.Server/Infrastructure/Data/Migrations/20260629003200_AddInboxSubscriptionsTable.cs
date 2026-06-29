using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Issue-285: project-scoped inbox subscription preferences — add the
    /// <c>InboxSubscriptions</c> table (PK ProjectId, 1:1 with Projects)
    /// with four bool columns (WorkflowFailedEnabled, ApprovalRequestedEnabled,
    /// IssueStartedEnabled, IssueCompletedEnabled) plus UpdatedAt. No data
    /// backfill — absence of a row is interpreted as all-four-enabled by the
    /// store layer.
    /// </summary>
    public partial class AddInboxSubscriptionsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InboxSubscriptions",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkflowFailedEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApprovalRequestedEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IssueStartedEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IssueCompletedEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxSubscriptions", x => x.ProjectId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxSubscriptions");
        }
    }
}
