namespace Mohist.Server.Issue.Domain;

public readonly record struct IssueRepositoryRef(string Value)
{
    public static IssueRepositoryRef? From(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new IssueRepositoryRef(value);

    public override string ToString() => Value;
}
