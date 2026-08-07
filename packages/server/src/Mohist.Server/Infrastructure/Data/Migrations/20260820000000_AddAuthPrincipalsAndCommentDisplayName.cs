using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Attribution anchors: agent principals are established when an Agent
/// definition is created and never deleted (archived Agents keep their
/// principal so historical activity keeps resolving). The comment
/// DisplayName column carries the caller-supplied display alias; the
/// Author column is the authenticated principal, so attribution never
/// comes from a self-declared parameter.
/// </summary>
[DbContext(typeof(MohistDbContext))]
[Migration("20260820000000_AddAuthPrincipalsAndCommentDisplayName")]
public partial class AddAuthPrincipalsAndCommentDisplayName : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Principals",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Principals", x => x.Id);
            });

        migrationBuilder.AddColumn<string>(
            name: "DisplayName",
            table: "IssueComments",
            type: "TEXT",
            maxLength: 100,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DisplayName",
            table: "IssueComments");

        migrationBuilder.DropTable(
            name: "Principals");
    }
}
