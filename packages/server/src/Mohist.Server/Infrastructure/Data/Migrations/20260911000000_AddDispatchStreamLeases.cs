using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Adds the DispatchStreamLeases table: dispatch worker coordination
/// state for event streams (claim/steal, attempt budget, backoff parking).
/// Rows are transient — present only while a stream is held or parked.
/// </summary>
public partial class AddDispatchStreamLeases : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DispatchStreamLeases",
            columns: table => new
            {
                Origin = table.Column<string>(maxLength: 32, nullable: false),
                Source = table.Column<string>(maxLength: 256, nullable: false),
                LeaseOwner = table.Column<string>(maxLength: 128, nullable: false),
                LeaseUntil = table.Column<DateTimeOffset>(nullable: false),
                Attempts = table.Column<int>(nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(nullable: true),
                LastError = table.Column<string>(nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DispatchStreamLeases", x => new { x.Origin, x.Source });
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "DispatchStreamLeases");
    }
}
