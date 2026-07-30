using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddSlackOwnerClaimCodeKind : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(
            name: "Kind",
            table: "SlackOwnerClaimCodes",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "initial");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "Kind",
            table: "SlackOwnerClaimCodes");
}
