using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

internal sealed record AgentHistoryProjectionSource(
    AgentSessionRecord Record,
    AgentSessionTranscriptData Transcript,
    string? ResolvedModel);

/// <summary>
/// Projects the existing Session-owned canonical input/turn records into the
/// public history contract. This is a read-only projection; lifecycle state
/// remains authoritative in AgentSession.Status.
/// </summary>
internal static class AgentHistoryProjector
{
    public static IReadOnlyList<AgentHistoryItemDto> Project(AgentHistoryProjectionSource source)
    {
        var session = source.Record.Session;
        var inputs = (session.Status.Inputs ?? [])
            .GroupBy(input => input.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var transcriptBySequence = source.Transcript.Turns
            .GroupBy(turn => turn.Sequence)
            .ToDictionary(group => group.Key, group => group.OrderBy(turn => turn.Id).First());
        var transcriptSequenceByTurnId = source.Transcript.Turns
            .ToDictionary(turn => turn.Id, turn => turn.Sequence);
        var modelBySequence = source.Transcript.Parts
            .Where(part => part.Type == TranscriptPartTypes.Model
                && transcriptSequenceByTurnId.ContainsKey(part.TurnId))
            .OrderBy(part => transcriptSequenceByTurnId[part.TurnId])
            .ThenBy(part => part.Sequence)
            .ThenBy(part => part.Id)
            .GroupBy(part => transcriptSequenceByTurnId[part.TurnId])
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(part => AgentSessionJsonHelper.GetStringProp(
                        AgentSessionJsonHelper.ParsePayload(part.PayloadJson),
                        "resolvedModel"))
                    .LastOrDefault(model => !string.IsNullOrWhiteSpace(model)));
        var usage = AgentSessionJsonHelper.Usage(session);
        var contextRefs = AgentSessionContextRefs.TryBuild(source.Record);
        var context = contextRefs is null
            ? null
            : new AgentHistoryContextDto(
                contextRefs.Value.IssueNumber,
                contextRefs.Value.EpicNumber,
                contextRefs.Value.Repository,
                contextRefs.Value.WorkspaceName);

        return (session.Status.Turns ?? [])
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Id))
            .OrderBy(turn => turn.Sequence)
            .ThenBy(turn => turn.Id, StringComparer.Ordinal)
            .Select(turn => ProjectTurn(
                source.Record,
                session,
                turn,
                inputs,
                transcriptBySequence.GetValueOrDefault(turn.Sequence),
                modelBySequence.GetValueOrDefault(turn.Sequence),
                source.ResolvedModel,
                usage,
                context,
                contextRefs?.WorkspaceName))
            .ToArray();
    }

    private static AgentHistoryItemDto ProjectTurn(
        AgentSessionRecord record,
        AgentSession session,
        AgentTurnRecord turn,
        IReadOnlyDictionary<string, AgentSessionInputRecord> inputs,
        AgentSessionTranscriptTurnRow? transcriptTurn,
        string? turnModel,
        string? resolvedModel,
        AgentUsageSummary usage,
        AgentHistoryContextDto? context,
        string? workspace)
    {
        var inputIds = turn.InputIds ?? [];
        var task = string.Join(
            "\n",
            inputIds
                .Where(inputs.ContainsKey)
                .Select(inputId => inputs[inputId].Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        if (string.IsNullOrWhiteSpace(task))
            task = inputIds.Count > 0 ? "Attachment input" : "Input unavailable";

        var startedAt = turn.RecordedAt ?? transcriptTurn?.StartedAt ?? session.Status.CreatedAt;
        var endedAt = IsTerminal(turn.Status)
            ? turn.UpdatedAt ?? transcriptTurn?.UpdatedAt
            : null;
        var durationMs = endedAt is DateTime ended && ended >= startedAt
            ? (long?)Math.Round((ended - startedAt).TotalMilliseconds)
            : null;
        var status = AgentSessionObservationMapper.TurnStatus(turn.Status);

        return new AgentHistoryItemDto(
            Id: turn.Id,
            SessionId: session.Id,
            InputId: inputIds.FirstOrDefault(),
            InputIds: inputIds,
            TurnId: turn.Id,
            JobId: turn.JobId,
            Task: task,
            Context: context,
            Status: status,
            Outcome: Outcome(turn.Status),
            Result: ToResult(turn.Result),
            StartedAt: startedAt.ToString("o"),
            EndedAt: endedAt?.ToString("o"),
            DurationMs: durationMs,
            Model: turnModel ?? resolvedModel ?? session.Settings.Model,
            Cost: new AgentHistoryCostDto(usage.CostAmount, usage.CostCurrency, "session"),
            Workspace: workspace,
            Target: record.Label(GenericAgentSessionMetadata.TargetId),
            Bucket: "recent");
    }

    private static bool IsTerminal(AgentTurnStatus status) =>
        status is AgentTurnStatus.Completed or AgentTurnStatus.Failed or AgentTurnStatus.Cancelled;

    private static string Outcome(AgentTurnStatus status) => status switch
    {
        AgentTurnStatus.Completed => "success",
        AgentTurnStatus.Failed => "failure",
        AgentTurnStatus.Cancelled => "cancelled",
        AgentTurnStatus.Queued or AgentTurnStatus.Executing => "pending",
        _ => "unknown",
    };

    private static AgentTurnResultObservationDto? ToResult(AgentTurnResult? result) => result is null
        ? null
        : new AgentTurnResultObservationDto(
            result.Message,
            result.Output,
            result.FailureReason,
            result.FailureCategory,
            result.ExitCode);
}

/// <summary>
/// Applies the history bucket contract after all Session turns have been
/// projected. A canonical Session/Turn key can only appear once, and Recent
/// is reserved for completed rows not already in Ended.
/// </summary>
internal static class AgentHistoryBucketReducer
{
    public static IReadOnlyList<AgentHistoryItemDto> Reduce(
        IEnumerable<AgentHistoryItemDto> items,
        int limit = 50,
        int recentLimit = 5)
    {
        var clampedLimit = Math.Clamp(limit, 1, 200);
        var clampedRecentLimit = Math.Clamp(recentLimit, 0, 50);
        var unique = items
            .GroupBy(item => (item.SessionId, item.TurnId))
            .Select(group => group
                .OrderByDescending(IsRicher)
                .ThenByDescending(item => item.StartedAt, StringComparer.Ordinal)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .First())
            .OrderByDescending(item => item.StartedAt, StringComparer.Ordinal)
            .ThenBy(item => item.SessionId, StringComparer.Ordinal)
            .ThenBy(item => item.TurnId, StringComparer.Ordinal)
            .ToList();

        var recentKeys = unique
            .Where(item => item.Status == "completed")
            .Take(clampedRecentLimit)
            .Select(item => (item.SessionId, item.TurnId))
            .ToHashSet();

        return unique
            .Take(clampedLimit)
            .Select(item => item with { Bucket = BucketFor(item, recentKeys) })
            .ToArray();
    }

    private static int IsRicher(AgentHistoryItemDto item) =>
        (item.Result is not null ? 4 : 0)
        + (item.EndedAt is not null ? 2 : 0)
        + (item.InputIds.Count > 0 ? 1 : 0);

    private static string BucketFor(
        AgentHistoryItemDto item,
        IReadOnlySet<(string SessionId, string TurnId)> recentKeys)
    {
        if (item.Status is "queued" or "executing") return "running";
        if (item.Status is "failed" or "cancelled") return "failed";
        if (item.Status == "unknown") return "unknown";
        if (recentKeys.Contains((item.SessionId, item.TurnId))) return "recent";
        if (item.Status == "completed") return "ended";
        return "recent";
    }
}
