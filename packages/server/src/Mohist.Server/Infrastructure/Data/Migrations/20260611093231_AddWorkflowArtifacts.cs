using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowArtifactPendingUploads",
                columns: table => new
                {
                    UploadId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    WorkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TaskRunId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: true),
                    StoragePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowArtifactPendingUploads", x => x.UploadId);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowArtifacts",
                columns: table => new
                {
                    ArtifactId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    WorkflowRunId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    TaskRunId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ArtifactStoragePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "file"),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: true),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowArtifacts", x => x.ArtifactId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowArtifactPendingUploads_ExpiresAt",
                table: "WorkflowArtifactPendingUploads",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowArtifactPendingUploads_IdempotencyKey",
                table: "WorkflowArtifactPendingUploads",
                columns: new[] { "WorkflowRunId", "WorkId", "TaskRunId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowArtifacts_IssueId_RecordedAt",
                table: "WorkflowArtifacts",
                columns: new[] { "IssueId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowArtifacts_WorkflowRunId_Path_RecordedAt",
                table: "WorkflowArtifacts",
                columns: new[] { "WorkflowRunId", "Path", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowArtifacts_WorkflowRunId_TaskRunId_RecordedAt",
                table: "WorkflowArtifacts",
                columns: new[] { "WorkflowRunId", "TaskRunId", "RecordedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowArtifactPendingUploads");

            migrationBuilder.DropTable(
                name: "WorkflowArtifacts");
        }
    }
}
