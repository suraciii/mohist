using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "ProjectPromptTemplates" (
                    "ProjectId" TEXT NOT NULL,
                    "Key" TEXT NOT NULL,
                    "DisplayName" TEXT NOT NULL,
                    "Description" TEXT NOT NULL,
                    "TagsJson" TEXT NOT NULL DEFAULT '[]',
                    "Stage" TEXT NULL,
                    "Body" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    CONSTRAINT "PK_ProjectPromptTemplates" PRIMARY KEY ("ProjectId", "Key")
                );
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_ProjectPromptTemplates_ProjectId_UpdatedAt"
                ON "ProjectPromptTemplates" ("ProjectId", "UpdatedAt");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectPromptTemplates");
        }
    }
}
