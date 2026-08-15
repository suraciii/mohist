using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Orleans;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Durable Server-side fence between a Workflow task attempt and a future
/// AgentJob launch. Preparing or accepting a handoff never starts runtime
/// work; a later activation slice owns materializing the AgentJob.
/// </summary>
public interface IWorkflowAgentHandoffGrain : IGrainWithStringKey
{
    Task<WorkflowAgentHandoffResult> PrepareAsync(WorkflowAgentHandoffCommand command);
    Task<WorkflowAgentHandoffResult> AcceptAsync(WorkflowAgentHandoffAcceptance acceptance);
    Task<WorkflowAgentHandoffPlan?> GetPlanAsync();
}

/// <summary>
/// Canonical rendered input for one Workflow dispatch work item. The command
/// id is the Workflow work id, so retries use the same durable grain and
/// fingerprint.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowAgentHandoffCommand(
    [property: Id(0)] string CommandId,
    [property: Id(1)] string ProjectId,
    [property: Id(2)] string WorkflowRunId,
    [property: Id(3)] string TaskRunId,
    [property: Id(4)] string AgentRef,
    [property: Id(5)] string Prompt,
    [property: Id(6)] string? Session = null,
    [property: Id(7)] long? TimeoutMilliseconds = null,
    [property: Id(8)] WorkflowAgentHandoffCompletionSnapshot? Completion = null);

/// <summary>
/// Frozen Workflow-owned completion input. It remains declarative until the
/// finalizer boundary is delivered; the handoff fence never evaluates it.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowAgentHandoffCompletionSnapshot(
    [property: Id(0)] string WorkId,
    [property: Id(1)] string Stage,
    [property: Id(2)] WorkflowAgentHandoffWorkspace? Workspace = null,
    [property: Id(3)] string? ExpectJson = null,
    [property: Id(4)] TaskArtifactCapture? Artifacts = null,
    [property: Id(5)] Dictionary<string, string>? SetVars = null,
    [property: Id(6)] RecoveryDefinition? Recovery = null,
    [property: Id(7)] int? RecoveryRemaining = null);

/// <summary>
/// The rendered workspace variant uses a name for issue-backed runs and the
/// immutable Workflow workspace identity for generic runs.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowAgentHandoffWorkspace(
    [property: Id(0)] string? Name = null,
    [property: Id(1)] WorkspaceIdentity? Identity = null);

/// <summary>
/// Immutable linkage reserved for a handoff. This is intentionally not an
/// AgentJob state mirror: it has no Runner, runtime-session, transcript, or
/// terminal fields.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowAgentInvocation(
    [property: Id(0)] string InvocationId,
    [property: Id(1)] string CommandId,
    [property: Id(2)] string ProjectId,
    [property: Id(3)] string WorkflowRunId,
    [property: Id(4)] string TaskRunId,
    [property: Id(5)] string JobKey,
    [property: Id(6)] string SessionId,
    [property: Id(7)] string InputId,
    [property: Id(8)] string TurnId);

public enum WorkflowAgentHandoffDisposition
{
    Prepared,
    Accepted,
    Rejected,
}

[GenerateSerializer]
public sealed record WorkflowAgentHandoffRejection(
    [property: Id(0)] string Code,
    [property: Id(1)] string Message);

/// <summary>
/// Persisted handoff record. The immutable Agent definition remains here,
/// next to the rendered command, so later activation cannot re-read mutable
/// Agent configuration after the first preflight decision.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowAgentHandoffPlan(
    [property: Id(0)] WorkflowAgentHandoffCommand Command,
    [property: Id(1)] string RequestFingerprint,
    [property: Id(2)] WorkflowAgentHandoffDisposition Disposition,
    [property: Id(3)] WorkflowAgentInvocation? Invocation,
    [property: Id(4)] AgentExecutionDefinition? ExecutionDefinition,
    [property: Id(5)] DateTimeOffset PreparedAt,
    [property: Id(6)] WorkflowAgentHandoffRejection? Rejection = null,
    [property: Id(7)] DateTimeOffset? AcceptedAt = null,
    [property: Id(8)] string? AgentId = null);

[GenerateSerializer]
public sealed record WorkflowAgentHandoffAcceptance(
    [property: Id(0)] string CommandId,
    [property: Id(1)] string RequestFingerprint);

[GenerateSerializer]
public sealed record WorkflowAgentHandoffResult(
    [property: Id(0)] WorkflowAgentHandoffDisposition Disposition,
    [property: Id(1)] WorkflowAgentInvocation? Invocation,
    [property: Id(2)] WorkflowAgentHandoffRejection? Rejection,
    [property: Id(3)] bool AlreadyPersisted);

[GenerateSerializer]
public sealed class WorkflowAgentHandoffState
{
    [Id(0)] public WorkflowAgentHandoffPlan? Plan { get; set; }
}

[Serializable]
[Orleans.GenerateSerializer]
public sealed class WorkflowAgentHandoffConflictException : Exception
{
    public WorkflowAgentHandoffConflictException(string commandId, string existingFingerprint)
        : base($"Workflow Agent handoff command '{commandId}' already stores different rendered input.")
    {
        CommandId = commandId;
        ExistingFingerprint = existingFingerprint;
    }

    [Orleans.Id(0)]
    public string CommandId { get; }

    [Orleans.Id(1)]
    public string ExistingFingerprint { get; }
}

public static class WorkflowAgentHandoffCodec
{
    public static string KeyFor(
        string projectId,
        string workflowRunId,
        string taskRunId,
        string commandId)
    {
        var identity = string.Join('\u001f',
            Require(projectId, nameof(projectId)),
            Require(workflowRunId, nameof(workflowRunId)),
            Require(taskRunId, nameof(taskRunId)),
            Require(commandId, nameof(commandId)));
        return $"workflow-agent-handoff/{StableToken(identity)}";
    }

    public static string Fingerprint(WorkflowAgentHandoffCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var canonical = string.Join('\u001f',
            command.CommandId ?? string.Empty,
            command.ProjectId ?? string.Empty,
            command.WorkflowRunId ?? string.Empty,
            command.TaskRunId ?? string.Empty,
            command.AgentRef ?? string.Empty,
            command.Prompt ?? string.Empty,
            command.Session ?? string.Empty,
            command.TimeoutMilliseconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            CanonicalCompletion(command.Completion));
        return Hash(canonical);
    }

    public static WorkflowAgentInvocation InvocationFor(WorkflowAgentHandoffCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var token = StableToken(string.Join('\u001f',
            command.ProjectId,
            command.WorkflowRunId,
            command.TaskRunId,
            command.CommandId));
        return new WorkflowAgentInvocation(
            InvocationId: $"workflow-agent-invocation-{token}",
            CommandId: command.CommandId,
            ProjectId: command.ProjectId,
            WorkflowRunId: command.WorkflowRunId,
            TaskRunId: command.TaskRunId,
            JobKey: $"agent-job-workflow-{token}",
            SessionId: $"agent-session-workflow-{token}",
            InputId: $"workflow-agent-input-{token}",
            TurnId: $"workflow-agent-turn-{token}");
    }

    private static string StableToken(string identity) => Hash(identity)[..32];

    private static string CanonicalCompletion(WorkflowAgentHandoffCompletionSnapshot? completion) =>
        completion is null
            ? "null"
            : CanonicalJson(completion with
            {
                ExpectJson = CanonicalizeEmbeddedJson(completion.ExpectJson),
            });

    private static string CanonicalizeEmbeddedJson(string? value)
    {
        if (value is null)
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(value);
            return CanonicalJson(document.RootElement);
        }
        catch (JsonException)
        {
            // Validation turns malformed declarations into a durable rejection.
            return value;
        }
    }

    private static string CanonicalJson<T>(T value) =>
        CanonicalJson(JSON.SerializeToElement(value));

    private static string CanonicalJson(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteCanonicalJson(element, writer);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCanonicalJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(
                    property => property.Name,
                    StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonicalJson(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string Hash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        return value;
    }
}
