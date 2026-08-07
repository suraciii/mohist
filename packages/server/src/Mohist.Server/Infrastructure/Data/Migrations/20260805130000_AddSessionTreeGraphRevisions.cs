using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260805130000_AddSessionTreeGraphRevisions")]
public partial class AddSessionTreeGraphRevisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SessionTreeGraphRevisions",
            columns: table => new
            {
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                PublishedRevision = table.Column<long>(type: "INTEGER", nullable: false),
                PublishedAt = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_SessionTreeGraphRevisions", x => x.ProjectId));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SessionTreeGraphRevisions");
    }
}
