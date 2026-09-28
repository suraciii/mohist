namespace Mohist.Server.Issue.Domain;

/// <summary>
/// Start-time rejection: the issue's target repository could not be
/// resolved (removed from the project, project missing, ambiguous
/// reference, ...). Distinct from <see cref="IssueStartBlockedException"/>
/// (draft/prerequisite blockers) so callers that must distinguish
/// "expected rejection, leave in backlog" from unexpected failures can
/// catch both by type.
/// </summary>
public sealed class IssueStartRepositoryUnavailableException : InvalidOperationException
{
    public IssueStartRepositoryUnavailableException(
        string message,
        string code = "repository_unavailable",
        string? field = null,
        string? repositoryName = null,
        string? baseBranch = null,
        string? nextStep = null)
        : base(message)
    {
        Code = code;
        Field = field;
        RepositoryName = repositoryName;
        BaseBranch = baseBranch;
        NextStep = nextStep;
    }

    public string Code { get; }
    public string? Field { get; }
    public string? RepositoryName { get; }
    public string? BaseBranch { get; }
    public string? NextStep { get; }
}
