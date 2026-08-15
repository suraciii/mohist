using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Adds the strict public Job snapshot and its source-revision checkpoint.
/// Existing internal jobs remain unprojected and the new direct route reports
/// projection_lag instead of exposing their canonical state JSON.
/// </summary>
public partial class AddDirectApiJobReadProjection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DirectApiProjectionJson",
            table: "AgentJobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "DirectApiProjectionRevision",
            table: "AgentJobs",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DirectApiProjectionJson",
            table: "AgentJobs");

        migrationBuilder.DropColumn(
            name: "DirectApiProjectionRevision",
            table: "AgentJobs");
    }
}
