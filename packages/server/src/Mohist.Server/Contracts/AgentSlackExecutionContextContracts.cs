using System.Security.Cryptography;
using System.Text;
using Orleans;

namespace Mohist.Server.Contracts;

public static class SlackCollaborationSkillCatalog
{
    public const string Name = "mohist-slack-collaboration";
    public const string Version = "1.0.0";

    private const string AssetSuffix = ".Agent.Services.Assets.mohist-slack-collaboration.skill.md";

    public static SlackCollaborationSkill Resolve()
    {
        var assembly = typeof(SlackCollaborationSkillCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(AssetSuffix, StringComparison.Ordinal));
        if (resourceName is null)
            throw new InvalidOperationException($"Embedded Slack collaboration asset '{AssetSuffix}' was not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded Slack collaboration asset '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream);
        var instructions = reader.ReadToEnd();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instructions))).ToLowerInvariant();
        return new SlackCollaborationSkill(Name, Version, instructions, hash);
    }
}

[GenerateSerializer]
public sealed record SlackReplyAnchor(
    [property: Id(0)] string WorkspaceId,
    [property: Id(1)] string ConversationId,
    [property: Id(2)] string ThreadRootMessageId,
    [property: Id(3)] string TriggeringMessageId,
    [property: Id(4)] string InitiatingMemberId,
    [property: Id(5)] string ConnectionId,
    [property: Id(6)] string SessionId,
    [property: Id(7)] string DispatchRef);

[GenerateSerializer]
public sealed record SlackCollaborationSkill(
    [property: Id(0)] string Name,
    [property: Id(1)] string Version,
    [property: Id(2)] string Instructions,
    [property: Id(3)] string ContentHash);

[GenerateSerializer]
public sealed record AgentSlackExecutionContext(
    [property: Id(0)] int Version,
    [property: Id(1)] SlackReplyAnchor ReplyAnchor,
    [property: Id(2)] SlackCollaborationSkill CollaborationSkill)
{
    public const int CurrentVersion = 1;
}

public static class SlackExecutionContextFactory
{
    public static AgentSlackExecutionContext Create(
        string workspaceId,
        string conversationId,
        string? threadRootMessageId,
        string triggeringMessageId,
        string initiatingMemberId,
        string connectionId,
        string sessionId,
        string dispatchRef) =>
        new(
            AgentSlackExecutionContext.CurrentVersion,
            new SlackReplyAnchor(
                workspaceId,
                conversationId,
                string.IsNullOrWhiteSpace(threadRootMessageId) ? triggeringMessageId : threadRootMessageId,
                triggeringMessageId,
                initiatingMemberId,
                connectionId,
                sessionId,
                dispatchRef),
            SlackCollaborationSkillCatalog.Resolve());
}
