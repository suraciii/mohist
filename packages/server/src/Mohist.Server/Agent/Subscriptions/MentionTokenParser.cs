namespace Mohist.Server.Agent.Subscriptions;

/// <summary>
/// Parses <c>@&lt;token&gt;</c> mention tokens out of an issue comment body.
/// A token is
/// <c>@</c> immediately followed by one or more name characters
/// (<c>[A-Za-z0-9_.-]</c>); the character before the <c>@</c> (when present)
/// must be whitespace or punctuation, so an <c>@</c> embedded in the middle
/// of a word (e.g. <c>foo@bar</c>) is not mistaken for a mention. The parser
/// dedupes tokens case-insensitively and returns them in first-occurrence
/// order, with the leading <c>@</c> stripped. The handler dedupes again by
/// resolved Agent id (two tokens that resolve to the same Agent launch once).
/// </summary>
internal static class MentionTokenParser
{
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
        // Comment bodies may contain large prompt text; keep token discovery
        // linear and independent of a wall-clock regex timeout.
        for (var atIndex = 0; atIndex < body.Length; atIndex++)
        {
            if (body[atIndex] != '@'
                || (atIndex > 0
                    && !char.IsWhiteSpace(body[atIndex - 1])
                    && !char.IsPunctuation(body[atIndex - 1])))
            {
                continue;
            }

            var tokenStart = atIndex + 1;
            if (tokenStart >= body.Length || !IsNameStart(body[tokenStart]))
                continue;

            var tokenEnd = tokenStart + 1;
            while (tokenEnd < body.Length && IsNamePart(body[tokenEnd]))
                tokenEnd++;

            while (tokenEnd > tokenStart
                && (body[tokenEnd - 1] == '.' || body[tokenEnd - 1] == '-'))
            {
                tokenEnd--;
            }

            var token = body[tokenStart..tokenEnd];
            if (seen.Add(token))
                tokens.Add(token);
        }
        return tokens;
    }

    private static bool IsNameStart(char value) =>
        (value is >= 'A' and <= 'Z')
        || (value is >= 'a' and <= 'z')
        || (value is >= '0' and <= '9')
        || value == '_';

    private static bool IsNamePart(char value) =>
        IsNameStart(value) || value is '.' or '-';
}
