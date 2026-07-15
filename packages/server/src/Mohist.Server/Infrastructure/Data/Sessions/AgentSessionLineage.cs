using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Services;
using DomainAgentSession = Mohist.Server.Sessions.Domain.AgentSession;

namespace Mohist.Server.Infrastructure.Data.Sessions;

/// <summary>
/// Pure helper that builds the lineage extensions dictionary for an
/// AgentSession CloudEvent from the producing session's own
/// <c>Metadata.Labels</c>. No cross-aggregate query is issued —
/// stamping uses only identity the session already holds (D6).
/// </summary>
/// <remarks>
/// Lineage attribute names live on <see cref="EventCatalog.Lineage"/> and
/// stay in sync with <c>design/event-protocol.md</c>. The matrix for
/// <c>agent-session.*</c>:
/// <list type="bullet">
/// <item><c>projectid</c> and <c>sessionid</c> are always stamped when
/// their label / id is present.</item>
/// <item><c>agentid</c> is stamped when the session originates from an
/// agent launch (label <c>mohist.io/agent-id</c> present).</item>
/// <item><c>issue</c>, <c>workflowrunid</c>, and <c>stage</c> are
/// stamped only for workflow/issue-origin sessions (those whose
/// <c>source-kind</c> label is <c>workflow</c>). Absent labels are
/// omitted, never an empty value.</item>
/// </list>
/// </remarks>
public static class AgentSessionLineage
{
    private const string WorkflowSourceKind = "workflow";

    /// <summary>
    /// Build the <c>extensions</c> dictionary for an agent-session event
    /// by projecting the session's own <c>Metadata.Labels</c> onto the
    /// protocol's lineage attribute names. Returns a dictionary
    /// (StringComparer.Ordinal) that may be empty when no lineage
    /// labels are present; absent affiliations are omitted, never an
    /// empty string.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildExtensions(DomainAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var extensions = new Dictionary<string, string>(StringComparer.Ordinal);
        var labels = session.Metadata.Labels;
        var projectId = RequiredProjectId(labels, session.Id);
        extensions[EventCatalog.Lineage.ProjectId] = projectId;

        var sourceKind = TryGetNonEmpty(labels!, AgentSessionQueryMetadataKeys.SourceKind, out var kind) ? kind : null;

        if (Equals(sourceKind, WorkflowSourceKind))
        {
            if (TryGetNonEmpty(labels!, AgentSessionQueryMetadataKeys.IssueNumber, out var issueNumber))
            {
                extensions[EventCatalog.Lineage.Issue] = issueNumber;
            }
            if (TryGetNonEmpty(labels!, AgentSessionQueryMetadataKeys.WorkflowRunId, out var workflowRunId))
            {
                extensions[EventCatalog.Lineage.WorkflowRunId] = workflowRunId;
            }
            if (TryGetNonEmpty(labels!, AgentSessionQueryMetadataKeys.Stage, out var stage))
            {
                extensions[EventCatalog.Lineage.Stage] = stage;
            }
        }
        else
        {
            if (TryGetNonEmpty(labels!, GenericAgentSessionMetadata.AgentId, out var agentId))
            {
                extensions[EventCatalog.Lineage.AgentId] = agentId;
            }
            if (Equals(sourceKind, "agent-launch")
                && TryGetNonEmpty(labels!, GenericAgentSessionMetadata.IssueNumber, out var issueNumber))
            {
                extensions[EventCatalog.Lineage.Issue] = issueNumber;
            }
        }

        StampSessionIdentity(extensions, session);

        return extensions;
    }

    private static void StampSessionIdentity(IDictionary<string, string> extensions, DomainAgentSession session)
    {
        if (!extensions.ContainsKey(EventCatalog.Lineage.SessionId) && !string.IsNullOrWhiteSpace(session.Id))
        {
            extensions[EventCatalog.Lineage.SessionId] = session.Id;
        }
    }

    private static bool TryGetNonEmpty(IReadOnlyDictionary<string, string> labels, string key, out string value)
    {
        if (labels.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static string RequiredProjectId(IReadOnlyDictionary<string, string>? labels, string sessionId)
    {
        if (labels is not null
            && TryGetNonEmpty(labels, AgentSessionQueryMetadataKeys.ProjectId, out var projectId))
        {
            return projectId;
        }

        throw new InvalidOperationException(
            $"Agent session '{sessionId}' cannot emit events without the required project-id label.");
    }
}
