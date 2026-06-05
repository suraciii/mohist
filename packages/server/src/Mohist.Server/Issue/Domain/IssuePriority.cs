namespace Mohist.Server.Issue.Domain;

public readonly record struct IssuePriority(string Value)
{
    public static IssuePriority Default { get; } = new("p2");

    public static IssuePriority From(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Default : new IssuePriority(value);

    public override string ToString() => Value;
}
