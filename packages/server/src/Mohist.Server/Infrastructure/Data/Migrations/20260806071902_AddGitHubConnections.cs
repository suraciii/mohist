using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260806071902_AddGitHubConnections")]
public partial class AddGitHubConnections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "GitHubConnections",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Owner = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Repo = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                RepositoryName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                IntakeLabel = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                FeedMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                ApproversJson = table.Column<string>(type: "JSON", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                IdentityKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                InstallationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GitHubConnections", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_GitHubConnections_Owner_Repo",
            table: "GitHubConnections",
            columns: new[] { "Owner", "Repo" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "GitHubConnections");
    }
}
