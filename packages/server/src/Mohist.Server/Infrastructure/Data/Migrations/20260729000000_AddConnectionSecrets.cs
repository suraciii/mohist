using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConnectionSecrets",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Blob = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectionSecrets", x => new { x.ProjectId, x.ConnectionId, x.Kind });
                    table.CheckConstraint(
                        "CK_ConnectionSecrets_Kind",
                        "\"Kind\" IN ('appToken', 'botToken')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionSecrets_ProjectId_ConnectionId",
                table: "ConnectionSecrets",
                columns: new[] { "ProjectId", "ConnectionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ConnectionSecrets");
        }
    }
}
