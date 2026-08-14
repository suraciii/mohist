using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// narrow Project-scoped, non-reentrant application
/// process manager. Mirrors the design pattern of
/// <c>IssueRepositoryCoordinatorGrain</c> but owned by the Workflow
/// profile reference surface: Project default writes, WorkflowRun
/// startup binding, and Profile deletion. The coordinator never touches
/// business facts; it persists only a single
/// <see cref="PendingWorkflowProfileCommand"/> fence per activation and
/// replays it on activation loss.
/// </summary>
public interface IWorkflowProfileReferenceCoordinatorGrain : IGrainWithStringKey
{
    /// <summary>
    /// Persist a new Project default WorkflowProfile ID. The Profile
    /// must already exist in the collection (built-in or custom); the
    /// participant re-validates membership before committing.
    /// </summary>
    Task<WorkflowProfileReferenceResult> SetProjectDefaultAsync(
        WorkflowProfileCommandPayload.SetProjectDefault payload,
        string commandId,
        long? expectedRevision);

    /// <summary>
    /// Persist the binding of a WorkflowRun to its selected Profile ID.
    /// The Profile must already exist in the collection; the participant
    /// re-validates membership and writes the nullable backing key only
    /// when the binding is a custom Profile.
    /// </summary>
    Task<WorkflowProfileReferenceResult> BindWorkflowRunAsync(
        WorkflowProfileCommandPayload.BindWorkflowRun payload,
        string commandId,
        long? expectedRevision);

    Task<WorkflowProfileReferenceResult> SetAgentActionOverrideAsync(
        WorkflowProfileCommandPayload.SetAgentActionOverride payload,
        string commandId,
        long? expectedRevision);

    Task<WorkflowProfileSaveResult> UpdateProfileAsync(
        WorkflowProfileCommandPayload.UpdateProfile payload,
        string commandId,
        long? expectedRevision);

    /// <summary>
    /// Delete a custom Profile after re-validating that no Project
    /// default, Issue selection (including terminal), or active
    /// WorkflowRun still references it. Built-in IDs are rejected with
    /// <see cref="WorkflowProfileReferenceResultCode.ProfileReadOnly"/>.
    /// </summary>
    Task<WorkflowProfileReferenceResult> DeleteProfileAsync(
        WorkflowProfileCommandPayload.DeleteProfile payload,
        string commandId,
        long? expectedRevision);

}

public enum WorkflowProfileReferenceResultCode
{
    Applied = 0,
    AlreadyApplied = 1,
    ProjectNotFound = 2,
    ProfileUnknown = 3,
    ProfileReadOnly = 4,
    BlockedByReferences = 5,
    StaleRevision = 6,
    ConflictingRequest = 7,
    ValidationFailed = 8,
}

[GenerateSerializer]
public sealed record WorkflowProfileReferenceResult(
    [property: Id(0)] WorkflowProfileReferenceResultCode Code,
    [property: Id(1)] string ProfileId,
    [property: Id(2)] long AppliedRevision,
    [property: Id(3)] string? Message = null,
    [property: Id(4)] WorkflowProfileDeletionBlockersDto? Blockers = null,
    [property: Id(5)] BoundWorkflowStart? Binding = null)
{
    public bool IsApplied =>
        Code is WorkflowProfileReferenceResultCode.Applied
            or WorkflowProfileReferenceResultCode.AlreadyApplied;
}

[GenerateSerializer]
public sealed record WorkflowProfileDeletionBlockersDto(
    [property: Id(0)] bool ProjectDefault,
    [property: Id(1)] IReadOnlyList<WorkflowProfileIssueBlockerDto> Issues,
    [property: Id(2)] IReadOnlyList<WorkflowProfileRunBlockerDto> ActiveRuns);

[GenerateSerializer]
public sealed record WorkflowProfileIssueBlockerDto(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] int IssueNumber,
    [property: Id(2)] string Status);

[GenerateSerializer]
public sealed record WorkflowProfileRunBlockerDto(
    [property: Id(0)] string WorkflowRunId,
    [property: Id(1)] string Status);

[GenerateSerializer]
public sealed record WorkflowProfileCoordinatorState(
    [property: Id(0)] PendingWorkflowProfileCommand? Pending)
{
    public static WorkflowProfileCoordinatorState Empty { get; } = new(Pending: null);
}

[GenerateSerializer]
public sealed record PendingWorkflowProfileCommand(
    [property: Id(0)] string CommandId,
    [property: Id(1)] string Kind,
    [property: Id(2)] string ProfileId,
    [property: Id(3)] long ExpectedRevision,
    [property: Id(4)] string PayloadJson);

public static class WorkflowProfileCommandPayloadKinds
{
    public const string SetProjectDefault = "setProjectDefault";
    public const string BindWorkflowRun = "bindWorkflowRun";
    public const string SetAgentActionOverride = "setAgentActionOverride";
    public const string UpdateProfile = "updateProfile";
    public const string DeleteProfile = "deleteProfile";
}

[GenerateSerializer]
public abstract record WorkflowProfileCommandPayload
{
    public abstract string Kind { get; }

    [GenerateSerializer]
    public sealed record SetProjectDefault(
        string ProjectId,
        string ProfileId) : WorkflowProfileCommandPayload
    {
        public override string Kind => WorkflowProfileCommandPayloadKinds.SetProjectDefault;
    }

    [GenerateSerializer]
    public sealed record BindWorkflowRun(
        string ProjectId,
        string WorkflowRunId,
        int? IssueNumber,
        int? EpicNumber,
        string? ExplicitProfileId,
        WorkflowRunMetadata Metadata,
        WorkspaceIdentity? Workspace = null,
        BoundWorkflowStart? Bound = null) : WorkflowProfileCommandPayload
    {
        public override string Kind => WorkflowProfileCommandPayloadKinds.BindWorkflowRun;
        public string ProfileId => Bound?.ProfileId ?? ExplicitProfileId ?? string.Empty;
    }

    [GenerateSerializer]
    public sealed record SetAgentActionOverride(
        string ProjectId,
        string ProfileId,
        string? AgentAction) : WorkflowProfileCommandPayload
    {
        public override string Kind => WorkflowProfileCommandPayloadKinds.SetAgentActionOverride;
    }

    [GenerateSerializer]
    public sealed record UpdateProfile(
        string ProjectId,
        string ProfileId,
        string Name,
        string Description,
        string DefinitionSource) : WorkflowProfileCommandPayload
    {
        public override string Kind => WorkflowProfileCommandPayloadKinds.UpdateProfile;
    }

    [GenerateSerializer]
    public sealed record DeleteProfile(
        string ProjectId,
        string ProfileId) : WorkflowProfileCommandPayload
    {
        public override string Kind => WorkflowProfileCommandPayloadKinds.DeleteProfile;
    }
}

[GenerateSerializer]
public sealed record BoundWorkflowStart(
    [property: Id(0)] string WorkflowRunId,
    [property: Id(1)] string ProjectId,
    [property: Id(2)] int? IssueNumber,
    [property: Id(3)] int? EpicNumber,
    [property: Id(4)] string? ExplicitProfileId,
    [property: Id(5)] string ProfileId,
    [property: Id(6)] string? AgentAction,
    [property: Id(7)] List<BoundStageStructure> Stages,
    [property: Id(8)] WorkflowRunMetadata Metadata,
    [property: Id(9)] WorkspaceIdentity? Workspace);

[GenerateSerializer]
public sealed record BoundStageStructure(
    [property: Id(0)] string Stage,
    [property: Id(1)] bool RequiresApproval);

internal static class WorkflowProfileCommandPayloadCodec
{
    public static string Serialize(WorkflowProfileCommandPayload payload)
    {
        var kind = payload.Kind;
        var data = JsonSerializer.Serialize(payload, payload.GetType(), JSON.Options);
        using var doc = JsonDocument.Parse(data);
        var envelope = new
        {
            kind,
            data = doc.RootElement.Clone(),
        };
        return JsonSerializer.Serialize(envelope, JSON.Options);
    }

    public static WorkflowProfileCommandPayload Deserialize(string kind, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var dataElement))
            throw new InvalidOperationException("WorkflowProfileCommandPayload envelope missing 'data' property");

        return kind switch
        {
            WorkflowProfileCommandPayloadKinds.SetProjectDefault =>
                JsonSerializer.Deserialize<WorkflowProfileCommandPayload.SetProjectDefault>(
                    dataElement.GetRawText(), JSON.Options)!,
            WorkflowProfileCommandPayloadKinds.BindWorkflowRun =>
                JsonSerializer.Deserialize<WorkflowProfileCommandPayload.BindWorkflowRun>(
                    dataElement.GetRawText(), JSON.Options)!,
            WorkflowProfileCommandPayloadKinds.SetAgentActionOverride =>
                JsonSerializer.Deserialize<WorkflowProfileCommandPayload.SetAgentActionOverride>(
                    dataElement.GetRawText(), JSON.Options)!,
            WorkflowProfileCommandPayloadKinds.UpdateProfile =>
                JsonSerializer.Deserialize<WorkflowProfileCommandPayload.UpdateProfile>(
                    dataElement.GetRawText(), JSON.Options)!,
            WorkflowProfileCommandPayloadKinds.DeleteProfile =>
                JsonSerializer.Deserialize<WorkflowProfileCommandPayload.DeleteProfile>(
                    dataElement.GetRawText(), JSON.Options)!,
            _ => throw new InvalidOperationException($"Unknown workflow profile command kind '{kind}'"),
        };
    }
}
