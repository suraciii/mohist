using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Persistence.Db;

#nullable disable

namespace Mohist.Server.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260604150000_MinimalWorkflowDomainEvents")]
public partial class MinimalWorkflowDomainEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "WorkflowEvents");

        migrationBuilder.CreateTable(
            name: "Events",
            columns: table => new
            {
                Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Id = table.Column<long>(type: "INTEGER", nullable: false),
                Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Data = table.Column<string>(type: "JSON", nullable: false),
                Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Events", x => new { x.Source, x.Id });
            });

        migrationBuilder.CreateIndex(
            name: "IX_Events_Type_Source_Id",
            table: "Events",
            columns: new[] { "Type", "Source", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
