namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Reusable type-pattern matcher shared by the in-memory event bus dispatch
/// loop (<see cref="InMemoryEventBus"/>) and the subscription dispatch
/// handler's <see cref="Mohist.Server.Events.Subscriptions.SubscriptionFilter"/>.
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
/// No other wildcard positions are supported; the bus <c>ValidateType</c>
/// path rejects malformed patterns at registration time so this matcher
/// can assume its input is well-formed.
/// </summary>
public static class CloudEventTypeMatcher
{
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
}