using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

internal static class TranscriptEventSummaryProjector
{
    public static AgentSessionTranscriptSummary Summarize(IEnumerable<TranscriptSummaryEvent> events)
    {
        string? resolvedModel = null;
        string? failureCategory = null;
        var toolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var failedToolCallIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in events)
        {
            if (e.Type == TranscriptPartTypes.Model)
            {
                var payload = AgentSessionJsonHelper.ParsePayload(e.PayloadJson);
                resolvedModel = AgentSessionJsonHelper.GetStringProp(payload, "resolvedModel") ?? resolvedModel;
            }
            else if (e.Type == TranscriptPartTypes.SessionClosed)
            {
                var payload = AgentSessionJsonHelper.ParsePayload(e.PayloadJson);
                failureCategory = AgentSessionJsonHelper.GetStringProp(payload, "failureCategory") ?? failureCategory;
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

        return new AgentSessionTranscriptSummary(
            resolvedModel,
            failureCategory,
            toolCallIds.Count == 0 ? null : toolCallIds.Count,
            failedToolCallIds.Count == 0 ? null : failedToolCallIds.Count);
    }
}

internal sealed record TranscriptSummaryEvent(long Sequence, string Type, string PayloadJson);
