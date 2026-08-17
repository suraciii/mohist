using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Project default execution configuration: one nullable JSON column
/// (<c>{ runtime, model, variant? }</c>) on Projects. Nullable and additive —
/// no rewrite, no backfill; deployments without a configured default observe
/// no behavior change.
/// </summary>
public partial class AddProjectDefaultExecutionConfig : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DefaultExecutionConfigJson",
            table: "Projects",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DefaultExecutionConfigJson",
            table: "Projects");
    }
}
