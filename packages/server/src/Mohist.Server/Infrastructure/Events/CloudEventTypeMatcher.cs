namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Reusable type-pattern matcher shared by subscription registration and
/// the subscription dispatch handler's
/// Encodes the simple extension defined in
/// <c>design/agent-subscriptions.md</c>:
/// <list type="bullet">
///   <item>exact match against the concrete type,</item>
///   <item><c>|</c>-separated alternatives (logical OR inside a single
///         pattern),</item>
///   <item><c>*</c> as a standalone alternative to match any type,</item>
///   <item><c>prefix.*</c> as a standalone alternative to match the prefix
///         itself and any <c>prefix.&lt;anything&gt;</c> sub-type.</item>
/// </list>
/// No other wildcard positions are supported; <see cref="ValidatePattern"/>
/// rejects malformed patterns at registration time so this matcher can
/// assume its input is well-formed.
/// </summary>
public static class CloudEventTypeMatcher
{
    public static void ValidatePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Subscription type must not be empty.", nameof(pattern));

        var alternatives = pattern.Split('|', StringSplitOptions.TrimEntries);
        if (alternatives.Any(string.IsNullOrEmpty))
            throw InvalidPattern(pattern);

        foreach (var alternative in alternatives)
        {
            if (alternative == "*")
                continue;
            if (alternative.EndsWith(".*", StringComparison.Ordinal))
            {
                if (alternative.Length == 2
                    || alternative.IndexOf('*') != alternative.Length - 1)
                {
                    throw InvalidPattern(pattern);
                }
                continue;
            }
            if (alternative.Contains('*'))
                throw InvalidPattern(pattern);
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="type"/> is matched by the
    /// supplied pipe-separated <paramref name="pattern"/>. Empty / whitespace
    /// patterns do not match anything.
    /// </summary>
    public static bool Matches(string? pattern, string? type)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(type))
            return false;

        foreach (var alternative in pattern.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (alternative.Length == 0)
                continue;
            if (alternative == type) return true;
            if (alternative == "*") return true;
            if (alternative.EndsWith(".*", StringComparison.Ordinal))
            {
                var prefix = alternative[..^2];
                if (type == prefix || type.StartsWith(prefix + ".", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static ArgumentException InvalidPattern(string pattern) =>
        new(
            $"Invalid subscription type '{pattern}': wildcards are only allowed as a standalone '*' or '.*' suffix.",
            nameof(pattern));
}
