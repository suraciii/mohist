namespace Mohist.Server.Project.Domain;

/// <summary>
/// issue-417 T-004: thrown when a repository removal is rejected because
/// at least one non-terminal Issue in the Project is still bound to the
/// target repository name. The grain / route surfaces this as a distinct
/// <c>repository_in_use</c> envelope without mutating Project or Issue
/// state.
/// </summary>
public sealed class RepositoryInUseException : InvalidOperationException
{
    public RepositoryInUseException(string repositoryName)
        : base($"Repository '{repositoryName}' is referenced by one or more non-terminal issues and cannot be removed")
    {
        RepositoryName = repositoryName;
    }

    public string RepositoryName { get; }
}

/// <summary>
/// issue-417 T-004: thrown when a Project repository participant command
/// is replayed with a stale revision (some other transition committed
/// in the meantime). Mirrors
/// <see cref="Mohist.Server.Issue.Domain.IssueRepositoryStaleRevisionException"/>
/// so the coordinator treats both surfaces uniformly.
/// </summary>
public sealed class ProjectRepositoryStaleRevisionException : Exception
{
    public ProjectRepositoryStaleRevisionException(string commandId, long expected, long actual)
        : base($"Project repository command '{commandId}' has stale revision: expected {expected}, actual {actual}")
    {
        CommandId = commandId;
        ExpectedRevision = expected;
        ActualRevision = actual;
    }

    public string CommandId { get; }
    public long ExpectedRevision { get; }
    public long ActualRevision { get; }
}

/// <summary>
/// issue-417 T-004: thrown when a Project repository participant
/// command is rejected because the named repository does not exist in
/// the Project. Distinct from a default-repository conflict so the
/// existing not-found / default precedence on the API surface is
/// preserved.
/// </summary>
public sealed class ProjectRepositoryNotFoundException : Exception
{
    public ProjectRepositoryNotFoundException(string repositoryName)
        : base($"Repository '{repositoryName}' not found in project")
    {
        RepositoryName = repositoryName;
    }

    public string RepositoryName { get; }
}
