namespace Mohist.Server.Project.Domain;

/// <summary>
/// receipt persisted alongside the Project aggregate
/// when a repository command is committed. Mirrors
/// <see cref="Mohist.Server.Issue.Domain.IssueRepositoryBindingReceipt"/>
/// but tracks Project-scoped repository transitions: add, metadata
/// update, default selection, and removal. The receipt is the single
/// authority for "is this command already applied" on replay so a lost
/// response cannot later race into a duplicate deletion or alias
/// rejection.
/// </summary>
public sealed record ProjectRepositoryCommandReceipt(
    string CommandId,
    string Kind,
    string RepositoryName,
    long AppliedRevision,
    DateTime AppliedAt);
