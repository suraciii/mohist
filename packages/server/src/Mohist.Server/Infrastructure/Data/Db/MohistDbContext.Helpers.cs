using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Infrastructure.Data.Db;

public partial class MohistDbContext
{
    private static bool DictionaryEqual(Dictionary<string, string>? left, Dictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var rightValue) || !string.Equals(value, rightValue, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    // Build a json_extract stored-column expression whose
    // path is keyed by a label-name constant. Returning the expression from
    // one helper means a rename in GenericAgentSessionMetadata is a
    // compile-time error rather than a silent SQL/metadata drift.
    private static string JsonExtractLabel(string key) =>
        $$"""json_extract("State", '$.metadata.labels."{{key}}"')""";

    private static int DictionaryHash(Dictionary<string, string>? value)
    {
        if (value is null) return 0;
        var hash = new HashCode();
        foreach (var entry in value.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            hash.Add(entry.Key, StringComparer.Ordinal);
            hash.Add(entry.Value, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }
}
