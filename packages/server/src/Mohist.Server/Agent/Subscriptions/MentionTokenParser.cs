using System.Text.RegularExpressions;

namespace Mohist.Server.Agent.Subscriptions;

/// <summary>
/// Parses <c>@&lt;token&gt;</c> mention tokens out of an issue comment body
/// (issue-490 design D4, spec <i>Token parsing</i>). A token is
/// <c>@</c> immediately followed by one or more name characters
/// (<c>[A-Za-z0-9_.-]</c>); the character before the <c>@</c> (when present)
/// must be whitespace or punctuation, so an <c>@</c> embedded in the middle
/// of a word (e.g. <c>foo@bar</c>) is not mistaken for a mention. The parser
/// dedupes tokens case-insensitively and returns them in first-occurrence
/// order, with the leading <c>@</c> stripped. The handler dedupes again by
/// resolved Agent id (two tokens that resolve to the same Agent launch once).
/// </summary>
internal static partial class MentionTokenParser
{
    private const string TokenGroupName = "token";

    /// <summary>
    /// Returns the distinct <c>@</c>-mention tokens from <paramref name="body"/>,
    /// with the leading <c>@</c> stripped, in first-occurrence order.
    /// Null / empty input returns an empty sequence.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = new List<string>();
        foreach (Match match in MentionRegex.Matches(body))
        {
            var token = match.Groups[TokenGroupName].Value;
            if (seen.Add(token))
                tokens.Add(token);
        }
        return tokens;
    }

    /// <summary>
    /// Matches <c>@&lt;name&gt;</c> where the leading <c>@</c> is preceded by
    /// start-of-string OR a single whitespace/punctuation char. The name
    /// starts and ends with <c>[A-Za-z0-9_]</c> and may contain
    /// <c>[A-Za-z0-9_.-]</c> in between, so a trailing sentence-period
    /// (<c>@supervisor.</c>) is treated as a delimiter, not part of the
    /// name, while a dot in the middle (<c>@supervisor.io</c>) stays in the
    /// token. The boundary prefix is part of the match (consumed) so
    /// consecutive mentions separated by one delimiter (<c>@a @b</c>) still
    /// both match. The match captures only the name token via
    /// <see cref="TokenGroupName"/>.
    /// </summary>
    [GeneratedRegex(
        @"(?:^|[\s\p{P}])@(?<token>[A-Za-z0-9_](?:[A-Za-z0-9_.\-]*[A-Za-z0-9_])?)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex MentionRegex { get; }
}
