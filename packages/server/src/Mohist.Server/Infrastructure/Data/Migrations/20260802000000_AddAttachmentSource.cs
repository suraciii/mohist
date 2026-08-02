using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddAttachmentSource : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Source",
            table: "Attachments",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE "Attachments" SET "Source" = 'upload' WHERE "Source" IS NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Source", table: "Attachments");
    }
}
