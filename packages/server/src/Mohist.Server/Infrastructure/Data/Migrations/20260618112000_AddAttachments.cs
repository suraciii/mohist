using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MohistDbContext))]
    [Migration("20260618112000_AddAttachments")]
    public partial class AddAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Older builds created this table outside EF because this migration
            // lacked DbContext metadata. The DDL must converge both histories.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "Attachments" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Attachments" PRIMARY KEY,
                    "ProjectId" TEXT NOT NULL,
                    "OwnerKind" TEXT NULL,
                    "OwnerId" TEXT NULL,
                    "OriginalFileName" TEXT NOT NULL,
                    "ContentType" TEXT NULL,
                    "Size" INTEGER NOT NULL,
                    "StoragePath" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "ExpiresAt" TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_Attachments_ExpiresAt"
                    ON "Attachments" ("ExpiresAt");
                CREATE INDEX IF NOT EXISTS "IX_Attachments_ProjectId_Owner"
                    ON "Attachments" ("ProjectId", "OwnerKind", "OwnerId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attachments");
        }
    }
}
