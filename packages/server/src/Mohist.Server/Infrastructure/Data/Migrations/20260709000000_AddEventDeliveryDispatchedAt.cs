using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventDeliveryDispatchedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DispatchedAt",
                table: "WorkflowRunEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DispatchedAt",
                table: "IssueEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DispatchedAt",
                table: "EpicEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRunEvents_Source_Id_DispatchedAt",
                table: "WorkflowRunEvents",
                columns: new[] { "Source", "Id", "DispatchedAt" },
                filter: "\"DispatchedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IssueEvents_Source_Id_DispatchedAt",
                table: "IssueEvents",
                columns: new[] { "Source", "Id", "DispatchedAt" },
                filter: "\"DispatchedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EpicEvents_Source_Id_DispatchedAt",
                table: "EpicEvents",
                columns: new[] { "Source", "Id", "DispatchedAt" },
                filter: "\"DispatchedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowRunEvents_Source_Id_DispatchedAt",
                table: "WorkflowRunEvents");

            migrationBuilder.DropIndex(
                name: "IX_IssueEvents_Source_Id_DispatchedAt",
                table: "IssueEvents");

            migrationBuilder.DropIndex(
                name: "IX_EpicEvents_Source_Id_DispatchedAt",
                table: "EpicEvents");

            migrationBuilder.DropColumn(
                name: "DispatchedAt",
                table: "WorkflowRunEvents");

            migrationBuilder.DropColumn(
                name: "DispatchedAt",
                table: "IssueEvents");

            migrationBuilder.DropColumn(
                name: "DispatchedAt",
                table: "EpicEvents");
        }
    }
}