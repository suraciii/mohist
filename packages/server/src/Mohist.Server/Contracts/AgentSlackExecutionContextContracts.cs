using System.Security.Cryptography;
using System.Text;
using Orleans;

namespace Mohist.Server.Contracts;

public static class AgentExecutionSources
{
    public const string Slack = "slack";
    public const string NonSlack = "non-slack";
    public const string Version1Capability = "execution-source-v1";
}

public static class SlackCollaborationSkillCatalog
{
    public const string Name = "mohist-slack-collaboration";
    public const string Version = "1.0.0";
    public const string ContentHash = "dedf18a796543ade06a9e0ece00c086577153e1e633f868c099b01cf910d641b";

    private const string AssetSuffix = ".Agent.Services.Assets.mohist-slack-collaboration.skill.md";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly IReadOnlyDictionary<string, string> PinnedContentHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Version] = ContentHash,
        };

    public static SlackCollaborationSkill Resolve()
    {
        var assembly = typeof(SlackCollaborationSkillCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(AssetSuffix, StringComparison.Ordinal));
        if (resourceName is null)
            throw new InvalidOperationException($"Embedded Slack collaboration asset '{AssetSuffix}' was not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded Slack collaboration asset '{resourceName}' could not be opened.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return ResolveEmbeddedBytes(buffer.ToArray());
    }

    internal static SlackCollaborationSkill ResolveAssetForTesting(string instructions) =>
        ResolveEmbeddedBytes(Utf8.GetBytes(instructions));

    private static SlackCollaborationSkill ResolveEmbeddedBytes(byte[] bytes)
    {
        if (!PinnedContentHashes.TryGetValue(Version, out var expectedHash))
            throw new InvalidOperationException($"No pinned Slack collaboration Skill digest exists for version '{Version}'.");

        var instructions = Utf8.GetString(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!StringComparer.Ordinal.Equals(hash, expectedHash))
        {
            throw new InvalidOperationException(
                $"Embedded Slack collaboration Skill version '{Version}' drifted from its pinned content digest.");
        }

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
    [property: Id(7)] string DispatchRef,
    // Manager executions use the synthetic project and owner explicitly so
    // reply routing cannot silently fall back to a Connection.
    [property: Id(8)] string? ProjectId = null,
    [property: Id(9)] string? OwnerKind = null);

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
        string threadRootMessageId,
        string triggeringMessageId,
        string initiatingMemberId,
        string connectionId,
        string sessionId,
        string dispatchRef,
        string? projectId = null,
        string? ownerKind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadRootMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggeringMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(initiatingMemberId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatchRef);

        return new(
            AgentSlackExecutionContext.CurrentVersion,
            new SlackReplyAnchor(
                workspaceId,
                conversationId,
                threadRootMessageId,
                triggeringMessageId,
                initiatingMemberId,
                connectionId,
                sessionId,
                dispatchRef,
                projectId,
                ownerKind),
            SlackCollaborationSkillCatalog.Resolve());
    }
}
