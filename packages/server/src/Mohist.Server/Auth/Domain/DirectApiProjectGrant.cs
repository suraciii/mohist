namespace Mohist.Server.Auth.Domain;

/// <summary>
/// The persisted Project boundary for the direct external Agent API.
/// Operator scope is deliberately not represented here: only an explicit
/// grant persisted on the credential can authorize that API.
/// </summary>
public enum DirectApiProjectGrantKind
{
    Explicit,
    OperatorAll,
}

public sealed record DirectApiProjectGrant(
    DirectApiProjectGrantKind Kind,
    IReadOnlyList<string> AllowedProjectIds)
{
    public static DirectApiProjectGrant Explicit(IEnumerable<string> projectIds) =>
        new(DirectApiProjectGrantKind.Explicit, projectIds.ToArray());

    public static DirectApiProjectGrant OperatorAll { get; } =
        new(DirectApiProjectGrantKind.OperatorAll, []);

    public bool IsValid => Kind switch
    {
        DirectApiProjectGrantKind.Explicit => AllowedProjectIds.Count > 0
            && AllowedProjectIds.All(id => !string.IsNullOrWhiteSpace(id))
            && AllowedProjectIds.Distinct(StringComparer.Ordinal).Count() == AllowedProjectIds.Count,
        DirectApiProjectGrantKind.OperatorAll => AllowedProjectIds.Count == 0,
        _ => false,
    };

    public string StorageValue => Kind switch
    {
        DirectApiProjectGrantKind.Explicit => "explicit",
        DirectApiProjectGrantKind.OperatorAll => "operator_all",
        _ => throw new InvalidOperationException("Unknown direct API Project grant kind."),
    };

    public static bool TryParse(
        string? value,
        out DirectApiProjectGrantKind kind)
    {
        kind = value switch
        {
            "explicit" => DirectApiProjectGrantKind.Explicit,
            "operator_all" => DirectApiProjectGrantKind.OperatorAll,
            _ => default,
        };
        return value is "explicit" or "operator_all";
    }
}
