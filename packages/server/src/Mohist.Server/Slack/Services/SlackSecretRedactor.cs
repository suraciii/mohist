using System.Text.RegularExpressions;

namespace Mohist.Server.Slack.Services;

public static class SlackSecretRedactor
{
    private static readonly Regex TokenPattern = new(
        @"(?i)(?<![A-Za-z0-9._-])(?:xapp|xoxe|xox[baprs])-[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string value, string replacement = "[REDACTED]") =>
        TokenPattern.Replace(value, replacement);
}
