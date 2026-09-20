using System.Security.Cryptography;
using System.Text;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// The resolved association a thread read is bound to. Every field comes from
/// recorded provenance and the durable thread mapping, never from the caller.
/// </summary>
public sealed record SlackThreadContinuationScope(
    string ProjectId,
    string SessionId,
    string ConnectionId,
    string WorkspaceTeamId,
    string ChannelId,
    string RootMessageId);

/// <summary>
/// Opaque continuation for one channel-thread traversal. The token carries the
/// provider cursor and is authenticated with the Connection's Bot credential
/// over the resolved scope, so a token issued for another Project, Session,
/// Connection, channel, or thread fails verification instead of continuing
/// someone else's discussion. Stateless: the provider cursor is the only
/// traversal state, and a fresh read starts a new traversal.
/// </summary>
public static class SlackThreadContinuation
{
    private const string Purpose = "mohist-slack-thread-continuation-v1";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Encode(string botToken, SlackThreadContinuationScope scope, string cursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        ValidateScope(scope);
        var canonical = Canonical(scope, cursor);
        return Base64UrlEncode(Utf8.GetBytes(cursor)) + "." + Sign(botToken, canonical);
    }

    /// <summary>
    /// Verifies the token against <paramref name="scope"/> and returns the
    /// provider cursor. A false result is deliberately indistinguishable for a
    /// malformed, tampered, or differently scoped token.
    /// </summary>
    public static bool TryDecode(
        string botToken,
        string? token,
        SlackThreadContinuationScope scope,
        out string? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(token))
            return false;
        try
        {
            ValidateScope(scope);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var separator = token.LastIndexOf('.');
        if (separator <= 0 || separator == token.Length - 1)
            return false;
        if (!TryBase64UrlDecode(token[..separator], out var cursorBytes))
            return false;

        string decoded;
        try
        {
            decoded = Utf8.GetString(cursorBytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(decoded))
            return false;

        var expected = Encoding.UTF8.GetBytes(Sign(botToken, Canonical(scope, decoded)));
        var supplied = Encoding.UTF8.GetBytes(token[(separator + 1)..]);
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
            return false;

        cursor = decoded;
        return true;
    }

    private static string Canonical(SlackThreadContinuationScope scope, string cursor) =>
        string.Join('\n',
            Purpose,
            scope.ProjectId,
            scope.SessionId,
            scope.ConnectionId,
            scope.WorkspaceTeamId,
            scope.ChannelId,
            scope.RootMessageId,
            cursor);

    private static string Sign(string botToken, string canonical) =>
        Convert.ToHexString(HMACSHA256.HashData(Utf8.GetBytes(botToken), Utf8.GetBytes(canonical)));

    private static void ValidateScope(SlackThreadContinuationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(scope.ProjectId)
            || string.IsNullOrWhiteSpace(scope.SessionId)
            || string.IsNullOrWhiteSpace(scope.ConnectionId)
            || string.IsNullOrWhiteSpace(scope.WorkspaceTeamId)
            || string.IsNullOrWhiteSpace(scope.ChannelId)
            || string.IsNullOrWhiteSpace(scope.RootMessageId))
        {
            throw new ArgumentException("The thread continuation scope is incomplete.", nameof(scope));
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryBase64UrlDecode(string token, out byte[] bytes)
    {
        bytes = [];
        if (token.Contains('=')
            || token.Any(character =>
                !(character is >= 'A' and <= 'Z'
                    or >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '-'
                    or '_'))
            || token.Length % 4 == 1)
        {
            return false;
        }

        var base64 = token.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
