using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueParentProjectionModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParentIssueNumber",
                table: "Issues",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ProjectId_ParentIssueNumber_Number",
                table: "Issues",
                columns: new[] { "ProjectId", "ParentIssueNumber", "Number" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Issues_ProjectId_ParentIssueNumber_Number",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "ParentIssueNumber",
                table: "Issues");
        }
    }
}
