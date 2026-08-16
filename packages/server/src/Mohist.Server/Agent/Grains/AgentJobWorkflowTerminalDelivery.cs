using System.Text.Json;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Agent.Grains;

/// <summary>
/// Durable obligation persisted on the AgentJob grain for delivering the
/// terminal of a workflow-originated job to the Workflow side over typed
/// transport. Staged by <c>EnterTerminalStateAsync</c> only when the job's
/// input carries the <see cref="AgentJobWorkflowInvocation"/> discriminator
/// (direct and routed launches stage nothing) and kept until the
/// <c>com.mohist.agent.job.workflow-terminal</c> CloudEvent append
/// succeeds; the <c>agent-job-recovery</c> reminder retries the emission.
/// <see cref="EventId"/> is the stable CloudEvent id
/// (<c>workflow-terminal:{jobKey}</c>) so retried or duplicated appends
/// collapse to the same envelope via the store-level (source, eventId)
/// dedup without a second outcome-shaping append.
/// </summary>
[GenerateSerializer]
public sealed record PendingWorkflowTerminalDelivery(
    [property: Id(0)] string EventId,
    [property: Id(1)] string InvocationId,
    [property: Id(2)] string? ProjectId,
    [property: Id(3)] string WorkflowRunId,
    [property: Id(4)] string TaskRunId,
    [property: Id(5)] string WorkId,
    [property: Id(6)] string JobId,
    [property: Id(7)] string? SessionId,
    [property: Id(8)] string? InputId,
    [property: Id(9)] string? TurnId,
    [property: Id(10)] AgentJobStatus Status,
    [property: Id(11)] string? Message,
    [property: Id(12)] string? Output,
    [property: Id(13)] string? FailureReason,
    [property: Id(14)] string? FailureCategory,
    [property: Id(15)] int? ExitCode,
    [property: Id(16)] string[]? ArtifactUploadIds,
    [property: Id(17)] AgentJobCompletionEvaluation? Evaluation,
    [property: Id(18)] DateTimeOffset RecordedAt);

/// <summary>
/// Typed completion evaluation the Runner's agent-job executor computed at
/// the execution boundary against the frozen task-level <c>expect</c>
/// (reusing <c>evaluateCompletion</c>): whether the workspace satisfied the
/// contract, the matched promise marker, the missing files / markers, the
/// <c>failIf</c> matches, and the human-readable message. The evaluation is
/// a Workflow completion fact, not an Agent execution fact: it rides the
/// terminal transport without changing the AgentJob's own terminal verdict.
/// </summary>
[GenerateSerializer]
public sealed record AgentJobCompletionEvaluation(
    [property: Id(0)] bool Satisfied,
    [property: Id(1)] string? Matched,
    [property: Id(2)] string[] MissingFiles,
    [property: Id(3)] AgentJobExpectationMarkerMiss[] MissingMarkers,
    [property: Id(4)] AgentJobExpectationFailIfMatch[] FailIfMatches,
    [property: Id(5)] string? Message);

[GenerateSerializer]
public sealed record AgentJobExpectationMarkerMiss(
    [property: Id(0)] string Path,
    [property: Id(1)] string Contains);

[GenerateSerializer]
public sealed record AgentJobExpectationFailIfMatch(
    [property: Id(0)] string Marker,
    [property: Id(1)] string FailIf,
    [property: Id(2)] string Path);

/// <summary>
/// Parses the typed <see cref="AgentJobCompletionEvaluation"/> from the
/// runner-reported terminal output. The agent-job executor embeds the
/// evaluation under the <c>expectation</c> key of the agent result's output
/// object (alongside the agent facts) so it arrives on the existing
/// AgentJob report channel without a new wire contract; this codec lifts it
/// into the typed record the workflow-terminal transport carries.
/// </summary>
public static class AgentJobCompletionEvaluationCodec
{
    public static AgentJobCompletionEvaluation? Parse(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
            return null;

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(outputJson).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("expectation", out var evaluation)
            || evaluation.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new AgentJobCompletionEvaluation(
            Satisfied: evaluation.TryGetProperty("satisfied", out var satisfied)
                && satisfied.ValueKind == JsonValueKind.True,
            Matched: evaluation.TryGetProperty("matched", out var matched)
                && matched.ValueKind == JsonValueKind.String
                ? matched.GetString()
                : null,
            MissingFiles: ReadStrings(evaluation, "missingFiles", item => item.GetProperty("path").GetString()),
            MissingMarkers: ReadEntries(
                evaluation,
                "missingMarkers",
                item => new AgentJobExpectationMarkerMiss(
                    item.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
                        ? path.GetString() ?? string.Empty
                        : string.Empty,
                    item.TryGetProperty("contains", out var contains) && contains.ValueKind == JsonValueKind.String
                        ? contains.GetString() ?? string.Empty
                        : string.Empty)),
            FailIfMatches: ReadEntries(
                evaluation,
                "failIfMatches",
                item => new AgentJobExpectationFailIfMatch(
                    item.TryGetProperty("marker", out var marker) && marker.ValueKind == JsonValueKind.String
                        ? marker.GetString() ?? string.Empty
                        : string.Empty,
                    item.TryGetProperty("failIf", out var failIf) && failIf.ValueKind == JsonValueKind.String
                        ? failIf.GetString() ?? string.Empty
                        : string.Empty,
                    item.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
                        ? path.GetString() ?? string.Empty
                        : string.Empty)),
            Message: evaluation.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null);
    }

    private static string[] ReadStrings(
        JsonElement evaluation,
        string property,
        Func<JsonElement, string?> selector)
    {
        if (!evaluation.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(selector)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static T[] ReadEntries<T>(
        JsonElement evaluation,
        string property,
        Func<JsonElement, T> selector)
    {
        if (!evaluation.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(selector)
            .ToArray();
    }
}
