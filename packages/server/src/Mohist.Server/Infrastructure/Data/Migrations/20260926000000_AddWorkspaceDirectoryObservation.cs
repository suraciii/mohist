using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration(MigrationId)]
public partial class AddWorkspaceDirectoryObservation : Migration
{
    public const string MigrationId = "20260926000000_AddWorkspaceDirectoryObservation";

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(
            name: "DirectoryObservationJson",
            table: "Workspaces",
            type: "TEXT",
            nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "DirectoryObservationJson", table: "Workspaces");
}
