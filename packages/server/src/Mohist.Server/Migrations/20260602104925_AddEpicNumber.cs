using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddEpicNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Epics" ADD COLUMN "Number" INTEGER NULL;
                """);

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "EpicCounters" (
                    "ProjectId" TEXT NOT NULL CONSTRAINT "PK_EpicCounters" PRIMARY KEY,
                    "Next" INTEGER NOT NULL
                );
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Epics_ProjectId_Number" ON "Epics" ("ProjectId", "Number");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_Epics_ProjectId_Number";
                """);

            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS "EpicCounters";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "Epics" DROP COLUMN "Number";
                """);
        }
    }
}
