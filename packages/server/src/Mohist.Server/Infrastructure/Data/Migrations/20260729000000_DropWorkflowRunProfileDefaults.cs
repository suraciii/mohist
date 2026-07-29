using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260729000000_DropWorkflowRunProfileDefaults")]
public partial class DropWorkflowRunProfileDefaults : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "DefaultVariables",
            table: "WorkflowRunProfiles");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(
            name: "DefaultVariables",
            table: "WorkflowRunProfiles",
            type: "TEXT",
            nullable: false,
            defaultValue: "{}");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        MohistDbContextModelSnapshot.BuildModelCore(modelBuilder);
}
