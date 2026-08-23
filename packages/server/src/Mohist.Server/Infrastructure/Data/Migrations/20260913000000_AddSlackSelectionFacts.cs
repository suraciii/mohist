using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Adds the durable facts and selection state to the existing ambiguity
/// claim. Existing advisory rows receive inert legacy defaults and are never
/// eligible for execution; no message facts are reconstructed or backfilled.
/// </summary>
public partial class AddSlackSelectionFacts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SenderSlackUserId",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            maxLength: 256,
            nullable: false,
            defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "TaskText",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            nullable: false,
            defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "FilesJson",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");
        migrationBuilder.AddColumn<string>(
            name: "AmbiguityKind",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "Legacy");
        migrationBuilder.AddColumn<string>(
            name: "CandidateReferencesJson",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");
        migrationBuilder.AddColumn<string>(
            name: "SelectionState",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "Pending");
        migrationBuilder.AddColumn<string>(
            name: "ChosenProjectId",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            maxLength: 256,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ChosenConnectionId",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            maxLength: 256,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "DispatchKind",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            maxLength: 32,
            nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DecidedAt",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "SelectionSessionId",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            maxLength: 512,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "SelectionInputId",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            maxLength: 128,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "SelectionTurnId",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            maxLength: 128,
            nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "AttemptCount",
            table: "SlackAmbiguousPrompts",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LastAttemptAt",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "FinishedAt",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "SettleReason",
            table: "SlackAmbiguousPrompts",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_SlackAmbiguousPrompts_ProjectId_SelectionState_UpdatedAt",
            table: "SlackAmbiguousPrompts",
            columns: new[] { "ProjectId", "SelectionState", "UpdatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_SlackAmbiguousPrompts_ProjectId_SelectionState_UpdatedAt",
            table: "SlackAmbiguousPrompts");
        foreach (var column in new[]
        {
            "SenderSlackUserId", "TaskText", "FilesJson", "AmbiguityKind",
            "CandidateReferencesJson", "SelectionState", "ChosenProjectId",
            "ChosenConnectionId", "DispatchKind", "DecidedAt", "SelectionSessionId",
            "SelectionInputId", "SelectionTurnId", "AttemptCount", "LastAttemptAt",
            "FinishedAt", "SettleReason",
        })
            migrationBuilder.DropColumn(name: column, table: "SlackAmbiguousPrompts");
    }
}
