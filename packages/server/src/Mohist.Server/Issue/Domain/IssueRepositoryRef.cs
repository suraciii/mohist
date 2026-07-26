namespace Mohist.Server.Issue.Domain;

/// <summary>
/// Canonical, Project-local name of a repository resource bound to an
/// Issue. Stored non-null for newly created Issues.
/// The aggregate never falls back to a default and never tries to
/// re-resolve through Project metadata; reads and the coordinator use
/// the stored value verbatim, which is what makes
/// <c>HasWorkflowStarted</c> a meaningful, race-free lock.
/// </summary>
public readonly record struct IssueRepositoryRef(string Value)
{
    public static IssueRepositoryRef? From(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new IssueRepositoryRef(value);

    public override string ToString() => Value;
}

/// <summary>
/// Receipt persisted alongside <see cref="Domain.Issue"/> coordination
/// state. A successful no-op reassignment still records a receipt so a
/// lost response cannot replay as a post-start lock failure on the
/// next coordinator activation. Rejected commands write no receipt;
/// the coordinator replays the command and observes the same
/// rejection at the same revision, or surfaces a stale-revision error
/// after another transition committed.
/// </summary>
public sealed record IssueRepositoryBindingReceipt(
    string CommandId,
    string Kind,
    string RepositoryName,
    long AppliedRevision,
    DateTime AppliedAt);

public sealed class IssueRepositoryLockedException : Exception
{
    public IssueRepositoryLockedException(int issueNumber)
        : base($"Issue #{issueNumber} has started workflow execution; target repository is locked")
    {
        IssueNumber = issueNumber;
    }

    public int IssueNumber { get; }
}

public sealed class IssueRepositoryUnknownException : Exception
{
    public IssueRepositoryUnknownException(string repositoryRef)
        : base($"Repository '{repositoryRef}' is not declared by the project")
    {
        RepositoryRef = repositoryRef;
    }

    public string RepositoryRef { get; }
}

public sealed class IssueRepositoryMissingOnReopenException : Exception
{
    public IssueRepositoryMissingOnReopenException(string repositoryRef)
        : base($"Issue cannot be reopened: stored target repository '{repositoryRef}' is no longer declared by the project")
    {
        RepositoryRef = repositoryRef;
    }

    public string RepositoryRef { get; }
}

public sealed class IssueRepositoryStaleRevisionException : Exception
{
    public IssueRepositoryStaleRevisionException(string commandId, long expected, long actual)
        : base($"Issue repository command '{commandId}' has stale revision: expected {expected}, actual {actual}")
    {
        CommandId = commandId;
        ExpectedRevision = expected;
        ActualRevision = actual;
    }

    public string CommandId { get; }
    public long ExpectedRevision { get; }
    public long ActualRevision { get; }
}