using System.Text.Json;

namespace Mohist.Server.Infrastructure;

public static class AgentPermissionVocabulary
{
    public static readonly IReadOnlyList<string> Terms =
    [
        "repo:read",
        "repo:write",
        "issue:read",
        "issue:write",
        "epic:read",
        "epic:write",
        "artifact:publish",
    ];

    private static readonly IReadOnlySet<string> AllowedTerms =
        new HashSet<string>(Terms, StringComparer.Ordinal);

    public static string? Validate(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object
            || !raw.TryGetProperty("permissions", out var permissions))
        {
            return null;
        }

        if (permissions.ValueKind != JsonValueKind.Array)
            return "permissions must be an array of declared permission terms.";

        foreach (var permission in permissions.EnumerateArray())
        {
            if (permission.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(permission.GetString()))
            {
                return $"permissions must contain non-empty terms from: {string.Join(", ", Terms)}.";
            }

            var value = permission.GetString()!;
            if (!AllowedTerms.Contains(value))
            {
                return $"permissions term '{value}' is not allowed; accepted terms: {string.Join(", ", Terms)}.";
            }
        }

        return null;
    }
}
