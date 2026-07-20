using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class RemoveIssueWorkflowProfilePrompts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "Prompts",
            table: "IssueWorkflowProfiles");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(
            name: "Prompts",
            table: "IssueWorkflowProfiles",
            type: "TEXT",
            nullable: false,
            defaultValue: "{}");
}
