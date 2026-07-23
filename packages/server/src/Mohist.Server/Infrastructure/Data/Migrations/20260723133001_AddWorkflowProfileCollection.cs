using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowProfileCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkflowProfileIdKey",
                table: "WorkflowRuns",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultWorkflowProfileId",
                table: "ProjectWorkflowProfiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultWorkflowProfileIdKey",
                table: "ProjectWorkflowProfiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowProfileIdKey",
                table: "Issues",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkflowProfileRecords",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    DefinitionSource = table.Column<string>(type: "TEXT", nullable: false),
                    SourceProvenance = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowProfileRecords", x => new { x.ProjectId, x.ProfileId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_MetadataProjectId_WorkflowProfileIdKey",
                table: "WorkflowRuns",
                columns: new[] { "MetadataProjectId", "WorkflowProfileIdKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ProjectId_WorkflowProfileIdKey",
                table: "Issues",
                columns: new[] { "ProjectId", "WorkflowProfileIdKey" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowProfileRecords_ProjectId",
                table: "WorkflowProfileRecords",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectWorkflowProfiles_ProjectId_DefaultWorkflowProfileIdKey",
                table: "ProjectWorkflowProfiles",
                columns: new[] { "ProjectId", "DefaultWorkflowProfileIdKey" });

            migrationBuilder.AddForeignKey(
                name: "FK_Issues_WorkflowProfileRecords_ProjectId_WorkflowProfileIdKey",
                table: "Issues",
                columns: new[] { "ProjectId", "WorkflowProfileIdKey" },
                principalTable: "WorkflowProfileRecords",
                principalColumns: new[] { "ProjectId", "ProfileId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectWorkflowProfiles_WorkflowProfileRecords_ProjectId_DefaultWorkflowProfileIdKey",
                table: "ProjectWorkflowProfiles",
                columns: new[] { "ProjectId", "DefaultWorkflowProfileIdKey" },
                principalTable: "WorkflowProfileRecords",
                principalColumns: new[] { "ProjectId", "ProfileId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowRuns_WorkflowProfileRecords_MetadataProjectId_WorkflowProfileIdKey",
                table: "WorkflowRuns",
                columns: new[] { "MetadataProjectId", "WorkflowProfileIdKey" },
                principalTable: "WorkflowProfileRecords",
                principalColumns: new[] { "ProjectId", "ProfileId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowProfileRecords");

            migrationBuilder.DropIndex(
                name: "IX_ProjectWorkflowProfiles_ProjectId_DefaultWorkflowProfileIdKey",
                table: "ProjectWorkflowProfiles");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_MetadataProjectId_WorkflowProfileIdKey",
                table: "WorkflowRuns");

            migrationBuilder.DropIndex(
                name: "IX_Issues_ProjectId_WorkflowProfileIdKey",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "WorkflowProfileIdKey",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "DefaultWorkflowProfileId",
                table: "ProjectWorkflowProfiles");

            migrationBuilder.DropColumn(
                name: "DefaultWorkflowProfileIdKey",
                table: "ProjectWorkflowProfiles");

            migrationBuilder.DropColumn(
                name: "WorkflowProfileIdKey",
                table: "Issues");
        }
    }
}
