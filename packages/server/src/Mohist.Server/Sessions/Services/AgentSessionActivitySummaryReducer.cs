using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

internal static class AgentSessionActivitySummaryReducer
{
    public static AgentSessionActivitySummaryState Reduce(
        AgentSessionActivitySummaryState state,
        IReadOnlyList<AgentSessionTranscriptPartDelta> mutations)
    {
        var source = state.Normalize();
        var sealedToolCallIds = new HashSet<string>(source.SealedToolCallIds, StringComparer.Ordinal);
        var sealedFailedToolCallIds = new HashSet<string>(source.SealedFailedToolCallIds, StringComparer.Ordinal);
        var currentParts = source.CurrentTurnParts
            .GroupBy(PartKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var currentTurnSequence = source.CurrentTurnSequence;
        var currentPartSequence = source.CurrentPartSequence;
        var resolvedModel = source.ResolvedModel;
        var latestActivity = source.LatestActivity;

        foreach (var mutation in mutations)
        {
            if (mutation.Type == TranscriptPartTypes.Input)
            {
                SealCurrentTurn(currentParts.Values, sealedToolCallIds, sealedFailedToolCallIds);
                currentTurnSequence = currentTurnSequence == 0 ? 1 : currentTurnSequence + 1;
                currentPartSequence = 0;
                currentParts.Clear();
                continue;
            }

            if (currentTurnSequence == 0)
            {
                currentTurnSequence = 1;
                currentPartSequence = 0;
            }

            var existingKey = PartKey(mutation.Type, mutation.CorrelationKey);
            AgentSessionActivitySummaryPart? existingPart = null;
            var replacesCurrentPart = mutation.Type is TranscriptPartTypes.Tool or TranscriptPartTypes.Model
                && currentParts.TryGetValue(existingKey, out existingPart);
            var partSequence = replacesCurrentPart ? existingPart!.Sequence : ++currentPartSequence;
            var part = replacesCurrentPart
                ? existingPart! with { PayloadJson = mutation.PayloadJson }
                : CreatePart(mutation, partSequence);

            if (mutation.Type is TranscriptPartTypes.Tool or TranscriptPartTypes.Model)
                currentParts[existingKey] = part;

            if (mutation.Type == TranscriptPartTypes.Model)
            {
                var payload = AgentSessionJsonHelper.ParsePayload(mutation.PayloadJson);
                resolvedModel = AgentSessionJsonHelper.GetStringProp(payload, "resolvedModel") ?? resolvedModel;
            }
            else if (mutation.Type == TranscriptPartTypes.SessionActivity)
            {
                var payload = AgentSessionJsonHelper.ParsePayload(mutation.PayloadJson);
                var candidate = new AgentSessionActivitySummaryCandidate(
                    currentTurnSequence,
                    partSequence,
                    part.PartId,
                    NullIfWhiteSpace(AgentSessionJsonHelper.GetStringProp(payload, "failureCategory")),
                    NullIfWhiteSpace(AgentSessionJsonHelper.GetStringProp(payload, "failureReason")));
                if (IsLater(latestActivity, candidate))
                    latestActivity = candidate;
            }
        }

        var toolCallIds = new HashSet<string>(sealedToolCallIds, StringComparer.Ordinal);
        var failedToolCallIds = new HashSet<string>(sealedFailedToolCallIds, StringComparer.Ordinal);
        foreach (var part in currentParts.Values.Where(part => part.Type == TranscriptPartTypes.Tool))
        {
            toolCallIds.Add(part.PartId);
            if (IsFailed(part.PayloadJson))
                failedToolCallIds.Add(part.PartId);
        }

        return source with
        {
            ResolvedModel = resolvedModel,
            FailureCategory = latestActivity?.FailureCategory,
            FailureReason = latestActivity?.FailureReason,
            ToolCallCount = toolCallIds.Count == 0 ? null : toolCallIds.Count,
            ToolErrorCount = failedToolCallIds.Count == 0 ? null : failedToolCallIds.Count,
            CurrentTurnSequence = currentTurnSequence,
            CurrentPartSequence = currentPartSequence,
            CurrentTurnParts = currentParts.Values
                .OrderBy(part => part.Sequence)
                .ThenBy(part => part.PartId, StringComparer.Ordinal)
                .ToArray(),
            SealedToolCallIds = sealedToolCallIds.Order(StringComparer.Ordinal).ToArray(),
            SealedFailedToolCallIds = sealedFailedToolCallIds.Order(StringComparer.Ordinal).ToArray(),
            LatestActivity = latestActivity,
        };
    }

    private static AgentSessionActivitySummaryPart CreatePart(
        AgentSessionTranscriptPartDelta mutation,
        long sequence)
    {
        var partId = mutation.Type == TranscriptPartTypes.Tool
            ? ExtractToolCallId(mutation.PayloadJson)
                ?? mutation.CorrelationId
                ?? $"{mutation.CorrelationKey}:{sequence}"
            : mutation.CorrelationId
                ?? $"{mutation.CorrelationKey}:{sequence}";
        return new AgentSessionActivitySummaryPart(
            mutation.Type,
            mutation.CorrelationKey,
            partId,
            sequence,
            mutation.PayloadJson);
    }

    private static void SealCurrentTurn(
        IEnumerable<AgentSessionActivitySummaryPart> currentParts,
        ISet<string> sealedToolCallIds,
        ISet<string> sealedFailedToolCallIds)
    {
        foreach (var part in currentParts.Where(part => part.Type == TranscriptPartTypes.Tool))
        {
            sealedToolCallIds.Add(part.PartId);
            if (IsFailed(part.PayloadJson))
                sealedFailedToolCallIds.Add(part.PartId);
        }
    }

    private static bool IsFailed(string payloadJson)
    {
        var payload = AgentSessionJsonHelper.ParsePayload(payloadJson);
        var status = AgentSessionJsonHelper.GetToolStringProp(payload, "status")
            ?? AgentSessionJsonHelper.GetToolStringProp(payload, "state");
        return string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractToolCallId(string payloadJson)
    {
        var payload = AgentSessionJsonHelper.ParsePayload(payloadJson);
        return AgentSessionJsonHelper.GetToolStringProp(payload, "toolCallId")
            ?? AgentSessionJsonHelper.GetToolStringProp(payload, "id")
            ?? AgentSessionJsonHelper.GetToolStringProp(payload, "callId");
    }

    private static string PartKey(AgentSessionActivitySummaryPart part) =>
        PartKey(part.Type, part.CorrelationKey);

    private static string PartKey(string type, string correlationKey) =>
        $"{type}\u001f{correlationKey}";

    private static bool IsLater(
        AgentSessionActivitySummaryCandidate? current,
        AgentSessionActivitySummaryCandidate candidate)
    {
        if (current is null) return true;
        var candidateKey = (candidate.TurnSequence, candidate.PartSequence, candidate.PartId);
        var currentKey = (current.TurnSequence, current.PartSequence, current.PartId);
        return candidateKey.CompareTo(currentKey) > 0;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
