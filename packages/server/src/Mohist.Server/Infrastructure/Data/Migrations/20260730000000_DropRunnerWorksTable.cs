using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class DropRunnerWorksTable : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RunnerWorks");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "RunnerWorks",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RunnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                OwnerKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                OwnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                WorkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                TakenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Reason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_RunnerWorks", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_RunnerWorks_RunnerId_Status",
            table: "RunnerWorks",
            columns: new[] { "RunnerId", "Status" });
        migrationBuilder.CreateIndex(
            name: "IX_RunnerWorks_RunnerId_OwnerKind_OwnerId_WorkId",
            table: "RunnerWorks",
            columns: new[] { "RunnerId", "OwnerKind", "OwnerId", "WorkId" });
    }
}
