using System.Security.Cryptography;
using System.Text;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Orleans;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Durable Server-side fence between a Workflow task attempt and an
/// AgentJob launch. Preparing or accepting a handoff never starts runtime
/// work; <see cref="ActivateAsync"/> materializes the reserved lineage from
/// an accepted receipt only.
/// </summary>
public interface IWorkflowAgentHandoffGrain : IGrainWithStringKey, IRemindable
{
    Task<WorkflowAgentHandoffResult> PrepareAsync(WorkflowAgentHandoffCommand command);
    Task<WorkflowAgentHandoffResult> AcceptAsync(WorkflowAgentHandoffAcceptance acceptance);

    /// <summary>
    /// Materializes the reserved AgentJob, AgentSession, first SessionInput,
    /// and first AgentTurn from the accepted receipt: PrepareJob →
    /// EnsureInitialLaunch → SubmitJob, driven by the persisted activation
    /// cursor so a crash or acknowledgement loss resumes at the same step
    /// under the same minted ids. Replaying an activated plan is a no-op
    /// that returns the same invocation. Prepared plans are refused;
    /// rejected plans replay their frozen rejection.
    /// </summary>
    Task<WorkflowAgentHandoffActivationResult> ActivateAsync();

    Task<WorkflowAgentHandoffPlan?> GetPlanAsync();
}

/// <summary>
/// Canonical rendered input for one Workflow dispatch work item. The command
/// id is the Workflow work id, so retries use the same durable grain and
/// fingerprint. <see cref="Expect"/> is the caller-rendered task-level
/// completion contract (serialized JSON, never parsed here) the Runner will
/// evaluate after the agent turn settles.
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
    [property: Id(8)] string? Expect = null);

/// <summary>
/// Per-invocation execution deadline resolved at activation render time
/// when the task input omits <c>timeout</c>. Matches the runtime action
/// default for inline <c>mohist/opencode</c> / <c>mohist/pi</c> turns (60
/// minutes) so a handoff execution is never bounded tighter than the inline
/// semantics it replaces — in particular not by the shorter global
/// <c>AgentJobOptions.JobTimeout</c> backstop.
/// </summary>
public static class WorkflowAgentHandoffDeadline
{
    public const long DefaultTimeoutMilliseconds = 3_600_000;
}

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
    /// <summary>
    /// Terminal activation disposition: the reserved participants exist
    /// under the minted identifiers and the job is submitted to shared
    /// admission.
    /// </summary>
    Activated,
}

[GenerateSerializer]
public sealed record WorkflowAgentHandoffRejection(
    [property: Id(0)] string Code,
    [property: Id(1)] string Message);

/// <summary>
/// Frozen run-scoped context resolved once from the WorkflowRun snapshot at
/// first preflight and never re-read afterwards. The workspace binding is
/// the named <c>issue-{n}</c> workspace for issue-linked runs, else the
/// run's free-form workspace path/branch; null when the run binds neither.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowAgentHandoffRunContext(
    [property: Id(0)] int? IssueNumber,
    [property: Id(1)] int? EpicNumber,
    [property: Id(2)] WorkflowAgentHandoffWorkspace? Workspace = null);

[GenerateSerializer]
public sealed record WorkflowAgentHandoffWorkspace(
    [property: Id(0)] string? Name,
    [property: Id(1)] string? Path,
    [property: Id(2)] string? Branch = null);

/// <summary>
/// Persisted handoff record. The immutable Agent definition and identity
/// remain here, next to the rendered command, so later activation cannot
/// re-read mutable Agent configuration after the first preflight decision.
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
    [property: Id(8)] string? AgentId = null,
    [property: Id(9)] string? AgentName = null,
    [property: Id(10)] string? SessionName = null,
    [property: Id(11)] WorkflowAgentHandoffRunContext? RunContext = null,
    [property: Id(12)] DateTimeOffset? ActivatedAt = null);

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

/// <summary>
/// Result of <see cref="IWorkflowAgentHandoffGrain.ActivateAsync"/>. A
/// replayed activation of an already-activated plan reports
/// <see cref="AlreadyActivated"/> with the same invocation.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowAgentHandoffActivationResult(
    [property: Id(0)] WorkflowAgentHandoffDisposition Disposition,
    [property: Id(1)] WorkflowAgentInvocation? Invocation,
    [property: Id(2)] bool AlreadyActivated);

/// <summary>
/// Participant step the durable activation cursor executes next. One step
/// at a time is persisted so a crash or acknowledgement loss resumes the
/// exact step the partial run left behind; every participant command is
/// idempotent under the minted ids.
/// </summary>
public enum WorkflowAgentHandoffActivationStep
{
    PrepareJob = 1,
    EnsureInitialLaunch = 2,
    SubmitJob = 3,
}

/// <summary>
/// Durable activation cursor. Created on the first
/// <see cref="IWorkflowAgentHandoffGrain.ActivateAsync"/> of an accepted
/// plan; <see cref="CompletedAt"/> marks the terminal Activated transition.
/// The recovery reminder resumes an incomplete cursor on activation or
/// crash.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowAgentHandoffActivation(
    [property: Id(0)] string CommandId,
    [property: Id(1)] WorkflowAgentHandoffActivationStep NextStep,
    [property: Id(2)] DateTimeOffset StartedAt,
    [property: Id(3)] DateTimeOffset? CompletedAt = null);

[GenerateSerializer]
public sealed class WorkflowAgentHandoffState
{
    [Id(0)] public WorkflowAgentHandoffPlan? Plan { get; set; }
    [Id(1)] public WorkflowAgentHandoffActivation? Activation { get; set; }
}

/// <summary>
/// Replays the frozen preflight rejection to an activation attempt. The
/// rejection is definitive: no replay can overturn it, even if the Agent or
/// its configuration changes.
/// </summary>
[Serializable]
[Orleans.GenerateSerializer]
public sealed class WorkflowAgentHandoffRejectedException : Exception
{
    public WorkflowAgentHandoffRejectedException(WorkflowAgentHandoffRejection rejection)
        : base($"Workflow Agent handoff is rejected ({rejection.Code}): {rejection.Message}")
    {
        Rejection = rejection;
    }

    [Orleans.Id(0)]
    public WorkflowAgentHandoffRejection Rejection { get; }
}

/// <summary>
/// Raised when activation cannot complete yet because a participant call or
/// probe failed. The persisted cursor and the recovery reminder keep
/// retrying; the caller retries with the same command identity.
/// </summary>
[Serializable]
[Orleans.GenerateSerializer]
public sealed class WorkflowAgentHandoffActivationPendingException : Exception
{
    public WorkflowAgentHandoffActivationPendingException(string commandId)
        : base("Workflow Agent handoff activation is still recovering. Retry with the same command identity.")
    {
        CommandId = commandId;
    }

    [Orleans.Id(0)]
    public string CommandId { get; }
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
            command.Expect ?? string.Empty);
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
