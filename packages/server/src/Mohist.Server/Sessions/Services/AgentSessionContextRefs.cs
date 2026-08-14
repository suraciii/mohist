using Mohist.Server.Infrastructure.Data.Sessions;

namespace Mohist.Server.Sessions.Services;

/// <summary>
/// Shared construction for the context-reference envelope on AgentSession
/// list items and generic-session summaries.
/// Reads the four launch labels (<see cref="GenericAgentSessionMetadata.IssueNumber"/>,
/// <see cref="GenericAgentSessionMetadata.EpicNumber"/>,
/// <see cref="GenericAgentSessionMetadata.Repository"/>,
/// <see cref="GenericAgentSessionMetadata.WorkspaceName"/>) with the same
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
        int? EpicNumber,
        string? Repository,
        string? WorkspaceName);

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
        var issueNumberText = record.Label(GenericAgentSessionMetadata.IssueNumber);
        var issueNumber = TryReadPositiveNumber(issueNumberText);
        var epicNumber = TryReadPositiveNumber(record.Label(GenericAgentSessionMetadata.EpicNumber));
        var repository = record.Label(GenericAgentSessionMetadata.Repository);
        var workspaceName = record.Label(GenericAgentSessionMetadata.WorkspaceName);

        if (issueNumber is null && epicNumber is null
            && string.IsNullOrWhiteSpace(repository)
            && string.IsNullOrWhiteSpace(workspaceName))
        {
            return null;
        }

        return new ContextRefs(issueNumber, epicNumber, repository, workspaceName);
    }

    private static int? TryReadPositiveNumber(string? value) =>
        int.TryParse(value, out var number) && number > 0 ? number : null;
}
