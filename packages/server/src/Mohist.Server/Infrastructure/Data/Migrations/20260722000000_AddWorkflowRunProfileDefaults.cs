using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddWorkflowRunProfileDefaults : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(
            name: "DefaultVariables",
            table: "WorkflowRunProfiles",
            type: "TEXT",
            nullable: false,
            defaultValue: "{}");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "DefaultVariables",
            table: "WorkflowRunProfiles");
}
