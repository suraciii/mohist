using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Infrastructure.PublicApi;

public sealed partial class PublicApiProjectionEngine
{
    // --- facts extraction ---

    private static PublicProjectionFacts BuildFacts(
        string sessionId,
        AgentSessionRow row,
        AgentSession session,
        IReadOnlyList<AgentJobRow> jobRows,
        IReadOnlyList<AgentSessionEventRow> journalRows)
    {
        var status = session.Status;
        return new PublicProjectionFacts
        {
            SessionId = sessionId,
            ProjectId = row.LabelProjectId,
            AgentId = row.LabelAgentId,
            Activity = status.Activity,
            SessionCreatedAt = ToUtc(status.CreatedAt),
            PendingStopActive = status.PendingStop?.IsActive == true,
            PendingResetActive = status.PendingReset is not null && status.PendingReset.Outcome is null,
            Jobs = jobRows
                .Select(jobRow => ToJobFacts(jobRow, TryDeserializeJobState(jobRow.State) ?? new AgentJobState()))
                .ToList(),
            Inputs = (status.Inputs ?? [])
                .Select(input => new PublicProjectionFacts.InputFacts(
                    input.Id,
                    input.Acceptance,
                    ToUtc(input.RecordedAt),
                    input.JobId))
                .ToList(),
            Turns = (status.Turns ?? [])
                .Select(turn => new PublicProjectionFacts.TurnFacts(
                    turn.Id,
                    turn.Status,
                    turn.InputIds ?? [],
                    turn.JobId,
                    ToUtc(turn.RecordedAt),
                    ToUtc(turn.UpdatedAt),
                    turn.Result))
                .ToList(),
            SessionJournal = journalRows
                .Select(journal => new PublicProjectionFacts.SessionJournalFacts(
                    journal.Id,
                    journal.Type,
                    journal.Time))
                .ToList(),
        };
    }

    private static PublicProjectionFacts BuildHistoricalFacts(
        string sessionId,
        AgentSessionRow row,
        AgentSessionLifecycleTransitionRow transition,
        IReadOnlyList<AgentSessionEventRow> journalRows,
        PublicProjectionFacts currentFacts)
    {
        AgentSessionLifecycleSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<AgentSessionLifecycleSnapshot>(transition.SnapshotJson, JSON.Options);
        }
        catch (JsonException)
        {
            snapshot = null;
        }

        if (snapshot is null)
        {
            return currentFacts;
        }

        return new PublicProjectionFacts
        {
            SessionId = sessionId,
            ProjectId = row.LabelProjectId,
            AgentId = row.LabelAgentId,
            Activity = snapshot.Activity,
            SessionCreatedAt = currentFacts.SessionCreatedAt,
            PendingStopActive = snapshot.PendingStopActive,
            PendingResetActive = snapshot.PendingResetActive,
            Jobs = snapshot.Jobs.Select(ToJobFacts).ToList(),
            Inputs = snapshot.Inputs
                .Select(input => new PublicProjectionFacts.InputFacts(
                    input.InputId,
                    input.Acceptance,
                    input.RecordedAt,
                    input.JobId))
                .ToList(),
            Turns = snapshot.Turns
                .Select(turn => new PublicProjectionFacts.TurnFacts(
                    turn.TurnId,
                    turn.Status,
                    turn.InputIds,
                    turn.JobId,
                    turn.RecordedAt,
                    turn.UpdatedAt,
                    turn.Result))
                .ToList(),
            SessionJournal = journalRows
                .Select(journal => new PublicProjectionFacts.SessionJournalFacts(
                    journal.Id,
                    journal.Type,
                    journal.Time))
                .ToList(),
        };
    }

    private static PublicProjectionFacts.JobFacts ToJobFacts(AgentSessionLifecycleJob job)
    {
        var status = Enum.TryParse<AgentJobStatus>(job.Status, ignoreCase: true, out var parsedStatus)
            ? parsedStatus
            : AgentJobStatus.Unknown;
        var terminalResult = job.TerminalStatus is null
            ? null
            : new AgentJobTerminalResult(
                Enum.TryParse<AgentJobStatus>(job.TerminalStatus, ignoreCase: true, out var terminalStatus)
                    ? terminalStatus
                    : AgentJobStatus.Unknown,
                job.TerminalMessage,
                job.TerminalOutput,
                null,
                job.TerminalFailureReason,
                job.TerminalExitCode);
        return new PublicProjectionFacts.JobFacts(
            job.JobKey,
            status,
            job.ProjectId,
            job.AgentId,
            job.SessionId,
            job.InitialInputId,
            job.InitialTurnId,
            job.SubmittedAt,
            job.ReadySince,
            job.RunningSince,
            job.TerminalAt,
            job.WaitingReason,
            terminalResult);
    }

    private static PublicProjectionFacts.JobFacts ToJobFacts(AgentJobRow row, AgentJobState state) => new(
        row.JobKey,
        state.Status,
        state.Input?.ProjectId ?? row.ProjectId,
        state.Input?.AgentId ?? row.AgentId,
        row.AgentSessionId,
        row.InitialInputId,
        row.InitialTurnId,
        state.SubmittedAt,
        ParseTimestamp(row.ReadySince),
        state.RunningSince,
        state.TerminalAt,
        state.WaitingReason,
        state.TerminalResult);

    private static AgentJobState? TryDeserializeJobState(string stateJson)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentJobState>(stateJson, JSON.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ToUtc(DateTime? value) =>
        value is null
            ? null
            : new DateTimeOffset(value.Value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
                : value.Value.ToUniversalTime());

    private static DateTimeOffset ToUtc(DateTime value) => ToUtc((DateTime?)value)!.Value;
}
