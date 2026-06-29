using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Issue-286: project inbox MVP — add the <c>InboxItems</c> read model that
    /// the inbox projection writes to and the project inbox HTTP API reads
    /// from. One row per CloudEvent that the projection accepted; idempotent
    /// by CloudEvent source plus id; list-served by a
    /// project-scoped CreatedAt-descending compound index. Schema-only; no
    /// data backfill.
    /// </summary>
    public partial class AddInboxItemsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InboxItems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    IssueTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false, defaultValue: ""),
                    NotificationKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceEventSource = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SourceEventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxItems", x => x.Id);
                    table.CheckConstraint("CK_InboxItems_NotificationKind", "\"NotificationKind\" IN ('workflow_failed', 'approval_requested', 'issue_started', 'issue_completed')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_ProjectId_CreatedAt",
                table: "InboxItems",
                columns: new[] { "ProjectId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "UQ_InboxItems_SourceEvent",
                table: "InboxItems",
                columns: new[] { "SourceEventSource", "SourceEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboxItems_ProjectId_Id",
                table: "InboxItems",
                columns: new[] { "ProjectId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxItems");
        }
    }
}
