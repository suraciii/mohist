using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260805140000_AddSlackAdapterLeases")]
public partial class AddSlackAdapterLeases : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SlackAdapterLeases",
            columns: table => new
            {
                TargetKey = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                Generation = table.Column<int>(type: "INTEGER", nullable: false),
                LeaseId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                LeaseKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                AdapterId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                IssuedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlackAdapterLeases", x => x.TargetKey);
                table.CheckConstraint(
                    "CK_SlackAdapterLeases_LeaseKind",
                    "\"LeaseKind\" IS NULL OR \"LeaseKind\" IN ('validation', 'runtime')");
                table.CheckConstraint(
                    "CK_SlackAdapterLeases_ActiveLeaseCoherent",
                    "(\"LeaseId\" IS NULL) = (\"LeaseKind\" IS NULL) AND " +
                    "(\"LeaseId\" IS NULL) = (\"AdapterId\" IS NULL) AND " +
                    "(\"LeaseId\" IS NULL) = (\"IssuedAt\" IS NULL) AND " +
                    "(\"LeaseId\" IS NULL) = (\"ExpiresAt\" IS NULL)");
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "SlackAdapterLeases");
}
