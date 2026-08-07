namespace Mohist.Server.Auth.Domain;

/// <summary>
/// One of the closed set of credential scopes. The set is deliberately
/// closed: a scope name unknown to this type is never accepted anywhere,
/// so a future scope can only appear by extending this type.
/// </summary>
public readonly struct Scope : IEquatable<Scope>
{
    public static readonly Scope Operator = new("operator");
    public static readonly Scope Readonly = new("readonly");
    public static readonly Scope Runner = new("runner");
    public static readonly Scope Webhook = new("webhook");

    private static readonly Scope[] All = [Operator, Readonly, Runner, Webhook];

    public string Name { get; }

    private Scope(string name) => Name = name;

    public static bool TryParse(string? name, out Scope scope)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                scope = candidate;
                return true;
            }
        }

        scope = default;
        return false;
    }

    public bool Equals(Scope other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Scope other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    public override string ToString() => Name;
}
