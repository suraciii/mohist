using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

internal static class TranscriptEventSummaryProjector
{
    /// <summary>
    /// Reduce a transcript into the per-session summary consumed by the
    /// generic-session read shape. The terminal-fact selection (failure
    /// reason + failure category) follows the AgentSession transcript
    /// turn order: parts are ordered by (turn sequence, part sequence,
    /// part id) and the latest applicable <c>session.closed</c> part
    /// wins for both fields. That ordering is preserved even when a new
    /// Runtime Session restarts the part-local sequence from 1; the
    /// outer turn-sequence key wins. The remaining projections (model
    /// resolution, tool counts) use last-write-wins by part sequence
    /// within the input order, matching the previous behaviour.
    /// </summary>
    public static AgentSessionTranscriptSummary Summarize(IEnumerable<TranscriptSummaryEvent> events)
    {
        string? resolvedModel = null;
        string? failureReason = null;
        string? failureCategory = null;
        var toolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var failedToolCallIds = new HashSet<string>(StringComparer.Ordinal);

        TranscriptSummaryEvent? latestClosed = null;
        foreach (var e in events)
        {
            if (e.Type == TranscriptPartTypes.Model)
            {
                var payload = AgentSessionJsonHelper.ParsePayload(e.PayloadJson);
                resolvedModel = AgentSessionJsonHelper.GetStringProp(payload, "resolvedModel") ?? resolvedModel;
            }
            else if (e.Type == TranscriptPartTypes.SessionActivity)
            {
                latestClosed = IsLater(latestClosed, e) ? e : latestClosed;
            }
            else if (e.Type == TranscriptPartTypes.Tool)
            {
                var payload = AgentSessionJsonHelper.ParsePayload(e.PayloadJson);
                var toolCallId = AgentSessionJsonHelper.GetToolStringProp(payload, "toolCallId")
                    ?? AgentSessionJsonHelper.GetToolStringProp(payload, "id")
                    ?? AgentSessionJsonHelper.GetToolStringProp(payload, "callId")
                    ?? e.Sequence.ToString();
                toolCallIds.Add(toolCallId);
                var status = AgentSessionJsonHelper.GetToolStringProp(payload, "status") ?? AgentSessionJsonHelper.GetToolStringProp(payload, "state");
                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    failedToolCallIds.Add(toolCallId);
            }
        }

        if (latestClosed is not null)
        {
            var payload = AgentSessionJsonHelper.ParsePayload(latestClosed.PayloadJson);
            // Reason and category MUST come from the same latest fact so
            // a current failure reason cannot be paired with a category
            // left by an older Runtime Session lineage entry
            // (issue-449 design decision 4).
            failureReason = AgentSessionJsonHelper.GetStringProp(payload, "failureReason");
            failureCategory = AgentSessionJsonHelper.GetStringProp(payload, "failureCategory");
        }

        return new AgentSessionTranscriptSummary(
            resolvedModel,
            failureCategory,
            toolCallIds.Count == 0 ? null : toolCallIds.Count,
            failedToolCallIds.Count == 0 ? null : failedToolCallIds.Count,
            string.IsNullOrWhiteSpace(failureReason) ? null : failureReason);
    }

    private static bool IsLater(TranscriptSummaryEvent? current, TranscriptSummaryEvent candidate)
    {
        if (current is null) return true;
        var candidateKey = (candidate.TurnSequence, candidate.Sequence, candidate.PartId);
        var currentKey = (current.TurnSequence, current.Sequence, current.PartId);
        return candidateKey.CompareTo(currentKey) > 0;
    }
}

internal sealed record TranscriptSummaryEvent(
    long TurnSequence,
    long Sequence,
    string PartId,
    string Type,
    string PayloadJson);
