using Mohist.Server.Infrastructure.Data.Sessions;

namespace Mohist.Server.Sessions.Services;

/// <summary>
/// Shared construction for the context-reference envelope on AgentSession
/// list items and generic-session summaries (issue-327 T-002 / design D4).
/// Reads the four launch labels (<see cref="GenericAgentSessionMetadata.IssueNumber"/>,
/// <see cref="GenericAgentSessionMetadata.EpicNumber"/>,
/// <see cref="GenericAgentSessionMetadata.Repository"/>,
/// <see cref="GenericAgentSessionMetadata.WorkspacePath"/>) with the same
/// parse-and-null-when-all-empty semantics, returns a nullable value tuple
/// so each caller maps the resolved labels to its own distinct DTO wire
/// shape (<see cref="AgentSessionListContextRefsDto"/> vs.
/// <see cref="GenericAgentSessionSummaryContextRefsDto"/>). Pure refactor:
/// the resolved envelope is byte-identical to the pre-consolidation result.
/// </summary>
internal static class AgentSessionContextRefs
{
    public readonly record struct ContextRefs(
        int? IssueNumber,
        string? EpicNumber,
        string? Repository,
        string? WorkspacePath);

    /// <summary>
    /// Reads the four launch labels from <paramref name="record"/> and
    /// returns the resolved <see cref="ContextRefs"/>, or <c>null</c> when
    /// every field is empty/whitespace — preserving the "absent rather
    /// than null object on the wire" invariant that both
    /// <see cref="AgentSessionQuerier.BuildAgentSessionListContextRefs"/>
    /// and
    /// <see cref="AgentSessionQuerier.BuildGenericSessionSummaryContextRefs"/>
    /// previously applied independently.
    /// </summary>
    public static ContextRefs? TryBuild(AgentSessionRecord record)
    {
        var issueNumberText = AgentSessionQuerier.Label(record, GenericAgentSessionMetadata.IssueNumber);
        var issueNumber = int.TryParse(issueNumberText, out var parsed) ? parsed : (int?)null;
        var epicNumber = AgentSessionQuerier.Label(record, GenericAgentSessionMetadata.EpicNumber);
        var repository = AgentSessionQuerier.Label(record, GenericAgentSessionMetadata.Repository);
        var workspacePath = AgentSessionQuerier.Label(record, GenericAgentSessionMetadata.WorkspacePath);

        if (issueNumber is null && string.IsNullOrWhiteSpace(epicNumber)
            && string.IsNullOrWhiteSpace(repository) && string.IsNullOrWhiteSpace(workspacePath))
        {
            return null;
        }

        return new ContextRefs(issueNumber, epicNumber, repository, workspacePath);
    }
}
