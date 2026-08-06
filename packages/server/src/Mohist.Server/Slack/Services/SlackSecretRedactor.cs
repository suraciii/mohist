using System.Text.RegularExpressions;

namespace Mohist.Server.Slack.Services;

public static class SlackSecretRedactor
{
    private static readonly Regex TokenPattern = new(@"(?i)(?:xoxb|xapp|xoxe|xoxp|xoxs)-[A-Za-z0-9._-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string value) => TokenPattern.Replace(value, "[REDACTED]");
}
