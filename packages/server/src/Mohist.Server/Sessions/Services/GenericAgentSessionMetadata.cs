using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

/// <summary>
/// Context for a generic (non-workflow) AgentSession produced from an Agent
/// profile launch. Used to build the session metadata that distinguishes
/// generic sessions from workflow sessions (<c>source-kind = agent-launch</c>)
/// and carries the resolved agent identity plus any optional context
/// references supplied at launch.
/// </summary>
/// <remarks>
/// The lookup keys recorded here intentionally exclude the workflow-shaped
/// keys (<c>workflow-run-id</c>, <c>session-name</c>) so the session is
/// reachable by its session id alone (no <c>workflowRunId</c> lookup key
/// required). Optional context refs (issue, epic, repository, workspace
/// path) are recorded as <c>Annotations</c> metadata only — they do NOT
/// create scope, mount, or supervisor lifecycle.
/// </remarks>
public sealed record GenericAgentSessionContext(
    string ProjectId,
    string AgentId,
    string AgentName,
    int? IssueNumber = null,
    int? EpicNumber = null,
    string? Repository = null,
    string? WorkspacePath = null,
    string? Title = null);

public static class GenericAgentSessionMetadata
{
    /// <summary>
    /// Key for the agent profile id (used as a lookup label so generic
    /// sessions can be filtered by agent).
    /// </summary>
    public const string AgentId = "mohist.io/agent-id";

    /// <summary>
    /// Key for the agent profile name (lookup label).
    /// </summary>
    public const string AgentName = "mohist.io/agent-name";

    /// <summary>
    /// Key for the optional issue context reference recorded on the
    /// session metadata. Does not create scope/mount/supervisor lifecycle.
    /// </summary>
    public const string IssueNumber = "mohist.io/agent-launch/issue-number";

    /// <summary>
    /// Key for the optional epic context reference recorded on the
    /// session metadata. Does not create scope/mount/supervisor lifecycle.
    /// </summary>
    public const string EpicNumber = "mohist.io/agent-launch/epic-number";

    /// <summary>
    /// Key for the optional repository context reference recorded on the
    /// session metadata. Does not create scope/mount/supervisor lifecycle.
    /// </summary>
    public const string Repository = "mohist.io/agent-launch/repository";

    /// <summary>
    /// Key for the optional workspace path context reference recorded on
    /// the session metadata. Does not create scope/mount/supervisor lifecycle.
    /// </summary>
    public const string WorkspacePath = "mohist.io/agent-launch/workspace-path";

    /// <summary>
    /// Label key identifying the CloudEvent that triggered a subscription-driven
    /// Agent launch. Recorded by <see cref="Mohist.Server.Agent.Services.IAgentLauncher"/>
    /// when the subscription dispatch handler invokes it with non-null
    /// <c>triggerLabels</c>; absent on manually launched sessions. Part of the
    /// agent-subscription-visibility bidirectional link so that an event can
    /// be looked up by id and the sessions it triggered can be enumerated.
    /// </summary>
    public const string TriggerEventId = "mohist.io/trigger/event-id";

    /// <summary>
    /// arbitration and triggered this Agent launch. Recorded alongside
    /// <see cref="TriggerEventId"/> by <see cref="Mohist.Server.Agent.Services.IAgentLauncher"/>;
    /// absent on manually launched sessions. Together with
    /// <see cref="TriggerEventId"/> this provides the forward link from session
    /// back to the triggering event and the subscription that caused it.
    /// </summary>
    public const string TriggerRuleId = "mohist.io/trigger/rule-id";

    /// <summary>
    /// Comment identity for a mention-driven launch (issue-490 T-002).
    /// Recorded by <see cref="Mohist.Server.Agent.Services.IAgentLauncher.LaunchMentionAsync"/>
    /// alongside <see cref="TriggerEventId"/> (the
    /// <c>com.mohist.issue.comment-added</c> event id) so the launch is
    /// traceable back to the originating comment from the AgentJob side and
    /// distinguishable from routing-rule / watch launches, which never set
    /// this label. Absent on manually launched sessions and on every
    /// non-mention subscription-driven launch.
    /// </summary>
    public const string TriggerCommentId = "mohist.io/trigger/comment-id";

    public static IReadOnlyDictionary<string, string> LookupLabels(GenericAgentSessionContext context) =>
        Labels(context);

    public static IReadOnlyDictionary<string, string> Labels(GenericAgentSessionContext context)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = context.ProjectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [AgentId] = context.AgentId,
            [AgentName] = context.AgentName,
        };
        if (context.IssueNumber is > 0)
            labels[IssueNumber] = context.IssueNumber.Value.ToString();
        if (context.EpicNumber is > 0)
            labels[EpicNumber] = context.EpicNumber.Value.ToString();
        if (!string.IsNullOrWhiteSpace(context.Repository))
            labels[Repository] = context.Repository!;
        if (!string.IsNullOrWhiteSpace(context.WorkspacePath))
            labels[WorkspacePath] = context.WorkspacePath!;
        return labels;
    }

    public static AgentSessionMetadata Metadata(GenericAgentSessionContext context)
    {
        IReadOnlyDictionary<string, string>? annotations = string.IsNullOrWhiteSpace(context.Title)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.Title] = context.Title!
            };
        return new AgentSessionMetadata(Labels(context), annotations);
    }
}
