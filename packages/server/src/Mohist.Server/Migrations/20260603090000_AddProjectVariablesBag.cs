using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Persistence.Db;

#nullable disable

namespace Mohist.Server.Migrations
{
    [DbContext(typeof(MohistDbContext))]
    [Migration("20260603090000_AddProjectVariablesBag")]
    public partial class AddProjectVariablesBag : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Projects" ADD COLUMN "VariablesJson" TEXT NOT NULL DEFAULT '{}';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VariablesJson",
                table: "Projects");
        }
    }
}
