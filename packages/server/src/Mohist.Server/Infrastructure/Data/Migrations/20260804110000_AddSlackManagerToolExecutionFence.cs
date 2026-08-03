using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260804110000_AddSlackManagerToolExecutionFence")]
public partial class AddSlackManagerToolExecutionFence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SlackManagerToolExecutionFences",
            columns: table => new
            {
                JobKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                SessionId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlackManagerToolExecutionFences", x => x.JobKey);
                table.CheckConstraint(
                    "CK_SlackManagerToolExecutionFences_State",
                    "\"State\" IN ('started', 'completed')");
            });

        migrationBuilder.CreateIndex(
            name: "IX_SlackManagerToolExecutionFences_SessionId",
            table: "SlackManagerToolExecutionFences",
            column: "SessionId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SlackManagerToolExecutionFences");
    }
}
