using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// RFC 8628 device authorizations: pending flows tracked by hashes of
/// the device code and user code, plus the session-family anchor on
/// issued credentials — every access/refresh token minted from one
/// device flow shares its FamilyId so a replay can revoke the whole
/// chain (RFC 9700 §4.14.2).
/// </summary>
public partial class AddDeviceAuthorizations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FamilyId",
            table: "Credentials",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "DeviceAuthorizations",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                DeviceCodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                UserCodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ClientName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                PrincipalId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                DecidedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DeviceAuthorizations", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Credentials_FamilyId",
            table: "Credentials",
            column: "FamilyId");

        migrationBuilder.CreateIndex(
            name: "IX_DeviceAuthorizations_DeviceCodeHash",
            table: "DeviceAuthorizations",
            column: "DeviceCodeHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_DeviceAuthorizations_UserCodeHash",
            table: "DeviceAuthorizations",
            column: "UserCodeHash",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "DeviceAuthorizations");

        migrationBuilder.DropIndex(
            name: "IX_Credentials_FamilyId",
            table: "Credentials");

        migrationBuilder.DropColumn(
            name: "FamilyId",
            table: "Credentials");
    }
}
