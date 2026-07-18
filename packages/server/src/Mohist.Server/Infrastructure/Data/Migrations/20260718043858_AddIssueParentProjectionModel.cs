using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddIssueParentProjectionModel : Migration
{
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
