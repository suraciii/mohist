using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260805150000_AddSlackLeaseCredentialFingerprint")]
public partial class AddSlackLeaseCredentialFingerprint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(
            name: "CredentialFingerprint",
            table: "SlackAdapterLeases",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.Sql(
                "ALTER TABLE \"SlackAdapterLeases\" DROP COLUMN \"CredentialFingerprint\";");
        }
        else
        {
            migrationBuilder.DropColumn(
                name: "CredentialFingerprint",
                table: "SlackAdapterLeases");
        }
    }
}
