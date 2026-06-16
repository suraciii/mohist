namespace Mohist.Server.Issue.Domain;

public readonly record struct IssueRisk(string Value)
{
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "low",
        "medium",
        "high",
    };

    public static IssueRisk? From(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (!Allowed.Contains(trimmed))
            throw new ArgumentException($"Risk must be one of: {string.Join(", ", Allowed)}", nameof(value));
        return new IssueRisk(trimmed.ToLowerInvariant());
    }

    public override string ToString() => Value;
}
