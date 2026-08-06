namespace Mohist.Server.Issue.Domain;

/// <summary>
/// Start-time rejection: the issue's target repository could not be
/// resolved (removed from the project, project missing, ambiguous
/// reference, ...). Distinct from <see cref="IssueStartBlockedException"/>
/// (draft/prerequisite blockers) so callers that must distinguish
/// "expected rejection, leave in backlog" from unexpected failures can
/// catch both by type.
/// </summary>
public sealed class IssueStartRepositoryUnavailableException(string message)
    : InvalidOperationException(message);
