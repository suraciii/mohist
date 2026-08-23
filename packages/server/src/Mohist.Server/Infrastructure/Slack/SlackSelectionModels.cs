using System.Text.Json.Serialization;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// The complete owning-Project/Connection identity of one Slack chooser
/// candidate. The pair is kept together so a later selection never has to
/// infer project ownership from a connection id.
/// </summary>
public sealed record SlackSelectionCandidateReference(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("connectionId")] string ConnectionId,
    [property: JsonPropertyName("botUserId")] string? BotUserId = null);

/// <summary>
/// The original facts captured atomically with an ambiguity claim.
/// TaskText may be empty when the message consists only of attachments, but
/// the value itself is always present.
/// </summary>
public sealed record SlackAmbiguousPromptFacts(
    string SenderSlackUserId,
    string TaskText,
    string FilesJson,
    string AmbiguityKind);

public static class SlackAmbiguityKinds
{
    public const string RootMultiMention = "RootMultiMention";
    public const string ThreadMultiMention = "ThreadMultiMention";
    public const string MultiBoundThreadReply = "MultiBoundThreadReply";
    public const string Legacy = "Legacy";

    public static bool IsDefined(string? value) => value is
        RootMultiMention or ThreadMultiMention or MultiBoundThreadReply or Legacy;
}

public static class SlackSelectionStates
{
    public const string Pending = "Pending";
    public const string Decided = "Decided";
    public const string Completed = "Completed";
    public const string Settled = "Settled";
}

public static class SlackSelectionDispatchKinds
{
    public const string RootLaunch = "RootLaunch";
    public const string ThreadLaunch = "ThreadLaunch";
    public const string ThreadFollowup = "ThreadFollowup";
}
