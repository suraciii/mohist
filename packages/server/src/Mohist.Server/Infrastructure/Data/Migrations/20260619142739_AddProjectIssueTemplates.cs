using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectIssueTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DisableDefaultIssueTemplate",
                table: "ProjectWorkflowProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ProjectIssueTemplates",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Template = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectIssueTemplates", x => new { x.ProjectId, x.Name });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectIssueTemplates_ProjectId",
                table: "ProjectIssueTemplates",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectIssueTemplates");

            migrationBuilder.DropColumn(
                name: "DisableDefaultIssueTemplate",
                table: "ProjectWorkflowProfiles");
        }
    }
}
