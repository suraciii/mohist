using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260806070541_AddIngressEvents")]
public partial class AddIngressEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "IngressEvents",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false),
                Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                DataContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Data = table.Column<string>(type: "JSON", nullable: false),
                ExtensionsJson = table.Column<string>(type: "JSON", nullable: false),
                DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IngressEvents", x => new { x.Source, x.Id });
            });

        migrationBuilder.CreateIndex(
            name: "IX_IngressEvents_Undelivered",
            table: "IngressEvents",
            columns: new[] { "Source", "Id" },
            filter: "\"DispatchedAt\" IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "IngressEvents");
    }
}
