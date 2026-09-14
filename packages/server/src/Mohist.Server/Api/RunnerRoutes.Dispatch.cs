using Mohist.Server.Infrastructure;
using System.Text.Json.Serialization;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Api;

/// <summary>
/// The owning aggregate identity is repeated outside the execution envelope
/// so a malformed work owner cannot prevent the Runner from settling the
/// dispatch through the report route.
/// </summary>
public record WorkDispatchReportOwner(
    string OwnerKind,
    string? WorkflowRunId = null,
    string? AgentJobId = null);

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
    /// identity for runtime events. Null for workflow dispatches.
    /// </summary>
    string? AgentSessionId = null,
    string? InitialInputId = null,
    string? InitialTurnId = null,
    string? Recovery = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? RecoveryRemaining = null,
    string? Expect = null,
    ParentIssueContextResponse? ParentIssueContext = null,
    AgentExecutionDefinition? AgentDefinition = null,
    AgentSessionStartup? AgentSessionStartup = null,
    string? ActionAttemptId = null,
    WorkDispatchReportOwner? ReportOwner = null,
    ManagerExecutionGrant? ManagerExecutionGrant = null,
    string? OriginMarker = null);
