using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration(MigrationId)]
public partial class AddProjectVerificationCommand : Migration
{
    public const string MigrationId = "20260923000000_AddProjectVerificationCommand";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "VerificationCommand",
            table: "Projects",
            type: "TEXT",
            maxLength: 4096,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "VerificationCommand",
            table: "Projects");
    }
}
