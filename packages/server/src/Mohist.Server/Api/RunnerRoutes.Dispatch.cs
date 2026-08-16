using Mohist.Server.Infrastructure;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Api;

public record WorkDispatchResponse(
    string WorkflowRunId,
    string WorkId,
    string? Uses,
    string? With,
    string? Variables,
    string WorkType,
    string? Stage,
    string? Title,
    string? ProjectId = null,
    int? IssueNumber = null,
    int? EpicNumber = null,
    string? Artifacts = null,
    string? SetVars = null,
    string? OwnerKind = null,
    string? AgentJobId = null,
    /// <summary>
    /// AgentSession id for the dispatch envelope. Set for agent-job
    /// dispatches whose launch minted a generic (non-workflow)
    /// AgentSession; the runner uses it verbatim as the session
    /// identity for runtime events. Null for workflow dispatches and
    /// AgentJob validation dispatches.
    /// </summary>
    string? AgentSessionId = null,
    string? Recovery = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? RecoveryRemaining = null,
    string? Expect = null,
    ParentIssueContextResponse? ParentIssueContext = null,
    AgentExecutionDefinition? AgentDefinition = null,
    AgentSessionStartup? AgentSessionStartup = null,
    string? TaskRunId = null,
    AgentRecoveryBinding? AgentRecovery = null,
    string? RunnerId = null,
    string? WorkspaceId = null,
    JsonElement? WorkspaceGeneration = null,
    string? WorkspaceHead = null,
    string? WorkspaceTree = null,
    IReadOnlyList<string>? CleanupScope = null);
