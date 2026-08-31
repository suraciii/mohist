using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration(MigrationId)]
public partial class RenameWorkflowArtifactActionAttempts : Migration
{
    public const string MigrationId = "20260922000000_RenameWorkflowArtifactActionAttempts";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_WorkflowArtifacts_WorkflowRunId_TaskRunId_RecordedAt",
            table: "WorkflowArtifacts");

        migrationBuilder.DropIndex(
            name: "UX_WorkflowArtifactPendingUploads_IdempotencyKey",
            table: "WorkflowArtifactPendingUploads");

        migrationBuilder.RenameColumn(
            name: "TaskRunId",
            table: "WorkflowArtifacts",
            newName: "ActionAttemptId");

        migrationBuilder.RenameColumn(
            name: "TaskRunId",
            table: "WorkflowArtifactPendingUploads",
            newName: "ActionAttemptId");

        migrationBuilder.CreateIndex(
            name: "IX_WorkflowArtifacts_WorkflowRunId_ActionAttemptId_RecordedAt",
            table: "WorkflowArtifacts",
            columns: new[] { "WorkflowRunId", "ActionAttemptId", "RecordedAt" });

        migrationBuilder.CreateIndex(
            name: "UX_WorkflowArtifactPendingUploads_IdempotencyKey",
            table: "WorkflowArtifactPendingUploads",
            columns: new[] { "WorkflowRunId", "WorkId", "ActionAttemptId", "Path" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_WorkflowArtifacts_WorkflowRunId_ActionAttemptId_RecordedAt",
            table: "WorkflowArtifacts");

        migrationBuilder.DropIndex(
            name: "UX_WorkflowArtifactPendingUploads_IdempotencyKey",
            table: "WorkflowArtifactPendingUploads");

        migrationBuilder.RenameColumn(
            name: "ActionAttemptId",
            table: "WorkflowArtifacts",
            newName: "TaskRunId");

        migrationBuilder.RenameColumn(
            name: "ActionAttemptId",
            table: "WorkflowArtifactPendingUploads",
            newName: "TaskRunId");

        migrationBuilder.CreateIndex(
            name: "IX_WorkflowArtifacts_WorkflowRunId_TaskRunId_RecordedAt",
            table: "WorkflowArtifacts",
            columns: new[] { "WorkflowRunId", "TaskRunId", "RecordedAt" });

        migrationBuilder.CreateIndex(
            name: "UX_WorkflowArtifactPendingUploads_IdempotencyKey",
            table: "WorkflowArtifactPendingUploads",
            columns: new[] { "WorkflowRunId", "WorkId", "TaskRunId", "Path" },
            unique: true);
    }
}
