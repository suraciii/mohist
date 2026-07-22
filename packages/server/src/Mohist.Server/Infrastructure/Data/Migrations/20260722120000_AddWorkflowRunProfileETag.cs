using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddWorkflowRunProfileETag : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<long>(
            name: "ETag",
            table: "WorkflowRunProfiles",
            type: "INTEGER",
            nullable: false,
            defaultValue: 1L);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "ETag",
            table: "WorkflowRunProfiles");
}
