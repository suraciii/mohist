using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class FixProjectIdSnapshotMismatch : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "ProjectId",
            table: "Issues",
            type: "TEXT",
            maxLength: 256,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldComputedColumnSql: "COALESCE(json_extract(State, '$.projectId'), json_extract(State, '$.ProjectId'))");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "ProjectId",
            table: "Issues",
            type: "TEXT",
            nullable: false,
            computedColumnSql: "COALESCE(json_extract(State, '$.projectId'), json_extract(State, '$.ProjectId'))",
            stored: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 256);
    }
}
