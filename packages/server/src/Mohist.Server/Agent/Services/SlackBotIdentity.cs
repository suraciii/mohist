using System.Security.Cryptography;
using System.Text;
using Mohist.Server.Agent.Domain;

namespace Mohist.Server.Agent.Services;

public sealed record SlackBotIdentityPreview(string BotName, string AppDescription);

public static class SlackBotIdentityDeriver
{
    private const int MaximumBotNameLength = 80;
    private const int StableSuffixLength = 8;
    private const string GenericAppDescription = "A Mohist Agent available in Slack.";

    public static SlackBotIdentityPreview Derive(Domain.Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return Derive(agent.Id, agent.Name, agent.Description);
    }

    public static SlackBotIdentityPreview Derive(AgentInfo agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return Derive(agent.Id, agent.Name, agent.Description);
    }

    internal static SlackBotIdentityPreview Derive(string agentId, string name, string description) =>
        new(
            IsValidBotName(name) ? name : BuildBotName(name, agentId),
            string.IsNullOrWhiteSpace(description) ? GenericAppDescription : description);

    private static bool IsValidBotName(string name) =>
        name.Length is > 0 and <= MaximumBotNameLength && name.All(IsAllowedBotNameCharacter);

    private static string BuildBotName(string name, string agentId)
    {
        var suffix = StableSuffix(agentId);
        var baseName = SanitizeBotName(name);
        if (baseName.Length == 0)
            baseName = "agent";

        var maximumBaseLength = MaximumBotNameLength - suffix.Length - 1;
        if (baseName.Length > maximumBaseLength)
            baseName = baseName[..maximumBaseLength].TrimEnd('-', '_', '.');
        if (baseName.Length == 0)
            baseName = "agent";

        return $"{baseName}-{suffix}";
    }

    private static string SanitizeBotName(string name)
    {
        var result = new StringBuilder(name.Length);
        var hasPendingSeparator = false;
        foreach (var character in name)
        {
            var normalized = character is >= 'A' and <= 'Z'
                ? (char)(character + ('a' - 'A'))
                : character;
            if (IsAllowedBotNameCharacter(normalized))
            {
                if (hasPendingSeparator && result.Length > 0 && result[^1] != '-')
                    result.Append('-');
                result.Append(normalized);
                hasPendingSeparator = false;
            }
            else
            {
                hasPendingSeparator = true;
            }
        }
        return result.ToString().Trim('-', '_', '.');
    }

    private static bool IsAllowedBotNameCharacter(char character) =>
        character is >= 'a' and <= 'z'
        or >= '0' and <= '9'
        or '-' or '_' or '.';

    private static string StableSuffix(string agentId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(agentId));
        return Convert.ToHexString(hash.AsSpan(0, StableSuffixLength / 2)).ToLowerInvariant();
    }
}
