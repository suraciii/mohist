namespace Mohist.Server.Slack.Services;

/// <summary>
/// Read port for one page of the Slack channel thread bound to an
/// AgentSession. The port returns normalized discussion facts only: the
/// infrastructure adapter is the single owner of the provider endpoint and
/// wire payload, and no Slack JSON, token, or internal URL crosses this
/// boundary. A read is a provider query with no other side effect.
/// </summary>
public interface ISlackThreadQueryPort
{
    Task<SlackThreadPage> ReadPageAsync(SlackThreadPageQuery query, CancellationToken ct = default);
}

/// <summary>
/// One provider page request. The caller supplies the already-resolved
/// conversation, thread root, and credential; the port never chooses a
/// channel.
/// </summary>
public sealed record SlackThreadPageQuery(
    string BotToken,
    string ConversationId,
    string ThreadRootTs,
    string? Continuation,
    int Limit);

public enum SlackThreadPageOutcome
{
    Ok,
    RateLimited,
    ProviderRejected,
    InvalidResponse,
    TransportError,
}

/// <summary>
/// One source-ordered Slack message. <see cref="Text"/> may be empty while the
/// message still carries content: <see cref="UnavailableContent"/> names the
/// parts this read did not retrieve (<c>file</c>, <c>attachment</c>,
/// <c>rich_text</c>), so an empty text field never reads as an empty message.
/// </summary>
public sealed record SlackThreadMessage(
    string Ts,
    string? AuthorUserId,
    string? AuthorBotId,
    string Text,
    bool ThreadRoot,
    bool Edited,
    string? EditedTs,
    bool Deleted,
    IReadOnlyList<string> UnavailableContent,
    string? Permalink);

/// <summary>
/// One provider page. <see cref="Continuation"/> is the provider cursor the
/// next page needs, or null when the provider returned no further messages for
/// this traversal; a short or empty page with a continuation is not completion.
/// <see cref="RetryAfter"/> is the delay the provider asked for on a
/// rate-limited page.
/// </summary>
public sealed record SlackThreadPage(
    SlackThreadPageOutcome Outcome,
    IReadOnlyList<SlackThreadMessage> Messages,
    string? Continuation = null,
    TimeSpan? RetryAfter = null,
    string? ErrorClass = null)
{
    public static SlackThreadPage Ok(IReadOnlyList<SlackThreadMessage> messages, string? continuation) =>
        new(SlackThreadPageOutcome.Ok, messages, continuation);

    public static SlackThreadPage RateLimited(TimeSpan? retryAfter) =>
        new(SlackThreadPageOutcome.RateLimited, [], RetryAfter: retryAfter);

    public static SlackThreadPage ProviderRejected(string errorClass) =>
        new(SlackThreadPageOutcome.ProviderRejected, [], ErrorClass: errorClass);

    public static SlackThreadPage InvalidResponse(string errorClass = "invalid_pagination_response") =>
        new(SlackThreadPageOutcome.InvalidResponse, [], ErrorClass: errorClass);

    public static SlackThreadPage TransportError { get; } =
        new(SlackThreadPageOutcome.TransportError, []);
}

/// <summary>
/// Deterministic port fake for service and route specs. The boundary test
/// treats fakes as part of the server assembly, never as a protocol client.
/// </summary>
public sealed class FakeSlackThreadQueryPort : ISlackThreadQueryPort
{
    public List<SlackThreadPageQuery> Queries { get; } = [];
    public SlackThreadPage Result { get; set; } = SlackThreadPage.Ok([], null);

    public Task<SlackThreadPage> ReadPageAsync(SlackThreadPageQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        Queries.Add(query);
        return Task.FromResult(Result);
    }
}
