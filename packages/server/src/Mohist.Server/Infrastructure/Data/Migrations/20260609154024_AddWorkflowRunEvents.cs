using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowRunEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowRunEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DataContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "JSON", nullable: false),
                    ExtensionsJson = table.Column<string>(type: "JSON", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRunEvents", x => new { x.Source, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRunEvents_Type_Source_Id",
                table: "WorkflowRunEvents",
                columns: new[] { "Type", "Source", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowRunEvents");
        }
    }
}
