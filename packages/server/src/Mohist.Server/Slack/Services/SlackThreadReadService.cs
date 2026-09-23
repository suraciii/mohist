using System.Text.Json.Serialization;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

public sealed record SlackThreadReadRequest(
    string ProjectId,
    string SessionId,
    int Limit,
    string? Continuation);

/// <summary>
/// The resolved thread identity of one read. It is the usable source
/// reference for every returned message: Workspace, Connection, channel, and
/// thread root as recorded, never a guessed permalink.
/// </summary>
public sealed record SlackThreadViewThread(
    string WorkspaceTeamId,
    string ConnectionId,
    string ChannelId,
    string RootMessageId);

public sealed record SlackThreadView(
    SlackThreadViewThread Thread,
    IReadOnlyList<SlackThreadMessage> Messages,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Continuation);

public sealed record SlackThreadReadError(
    string Code,
    string Message,
    TimeSpan? RetryAfter = null,
    string? ProviderError = null);

public sealed record SlackThreadReadResult(SlackThreadView? View, SlackThreadReadError? Error)
{
    public static SlackThreadReadResult Read(SlackThreadView view) => new(view, null);

    public static SlackThreadReadResult Fail(
        string code,
        string message,
        TimeSpan? retryAfter = null,
        string? providerError = null) =>
        new(null, new SlackThreadReadError(code, message, retryAfter, providerError));
}

/// <summary>
/// Owns one channel-thread read: resolve the Session's recorded binding, prove
/// the Connection still holds a usable verified Bot identity, verify the
/// continuation against that exact binding, and read exactly one provider page.
/// A read creates no Job, Input, or Turn; it never sends Slack traffic beyond
/// the provider query and never substitutes another thread or transcript.
/// </summary>
public sealed class SlackThreadReadService(
    SlackThreadReadBindingResolver bindings,
    AgentConnectionStore connections,
    SlackAdapterLeaseService leases,
    ISlackThreadQueryPort threads) : IScopedService
{
    public const int DefaultLimit = 15;
    public const int MaxLimit = 100;

    public async Task<SlackThreadReadResult> ReadAsync(
        SlackThreadReadRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ProjectId) || string.IsNullOrWhiteSpace(request.SessionId))
            return SlackThreadReadResult.Fail("invalid_request", "project and sessionId are required.");
        if (request.Limit is < 1 or > MaxLimit)
            return SlackThreadReadResult.Fail("invalid_request", $"limit must be between 1 and {MaxLimit}.");

        var target = await bindings.ResolveAsync(request.ProjectId, request.SessionId, ct);
        if (target.Target is null)
            return SlackThreadReadResult.Fail(TargetErrorCode(target.Outcome), target.Reason ?? string.Empty);
        var thread = target.Target;

        var connection = await connections.GetAsync(request.ProjectId, thread.ConnectionId, ct);
        if (connection is null || connection.DeletedAt is not null)
        {
            return SlackThreadReadResult.Fail(
                "connection_unavailable",
                "The Slack Connection bound to this Session no longer exists.");
        }
        if (connection.DesiredState == DesiredStateKind.Disabled)
        {
            return SlackThreadReadResult.Fail(
                "connection_unavailable",
                "The Slack Connection bound to this Session is disabled.");
        }
        if (string.IsNullOrWhiteSpace(connection.BotUserId) || string.IsNullOrWhiteSpace(connection.AppId))
        {
            return SlackThreadReadResult.Fail(
                "connection_unavailable",
                "The Slack Connection has no verified Bot identity; re-run `mo slack install-agent` before reading.");
        }

        var botToken = await leases.ResolveVerifiedBotTokenForServerAsync(
            new SlackLeaseTargetRef.Connection(request.ProjectId, connection.Id),
            ct);
        if (string.IsNullOrWhiteSpace(botToken))
        {
            return SlackThreadReadResult.Fail(
                "credential_unavailable",
                "The Slack Connection has no verified Bot credential right now; reconnect the Slack adapter and retry.");
        }

        var scope = new SlackThreadContinuationScope(
            request.ProjectId,
            request.SessionId,
            connection.Id,
            thread.WorkspaceTeamId,
            thread.ChannelId,
            thread.RootMessageId);
        string? cursor = null;
        if (!string.IsNullOrWhiteSpace(request.Continuation)
            && !SlackThreadContinuation.TryDecode(botToken, request.Continuation, scope, out cursor))
        {
            return SlackThreadReadResult.Fail(
                "continuation_invalid",
                "The continuation does not belong to this Session's Slack thread; start a new read without --continuation.");
        }

        var page = await threads.ReadPageAsync(
            new SlackThreadPageQuery(botToken, thread.ChannelId, thread.RootMessageId, cursor, request.Limit),
            ct);

        var view = new SlackThreadViewThread(
            thread.WorkspaceTeamId,
            connection.Id,
            thread.ChannelId,
            thread.RootMessageId);
        return page.Outcome switch
        {
            SlackThreadPageOutcome.Ok => SlackThreadReadResult.Read(new SlackThreadView(
                view,
                page.Messages,
                page.Continuation is null
                    ? null
                    : SlackThreadContinuation.Encode(botToken, scope, page.Continuation))),
            SlackThreadPageOutcome.RateLimited => SlackThreadReadResult.Fail(
                "rate_limited",
                page.RetryAfter is { } retryAfter
                    ? $"Slack is rate-limiting this thread read; retry after {(int)Math.Ceiling(retryAfter.TotalSeconds)} seconds."
                    : "Slack is rate-limiting this thread read; retry shortly.",
                page.RetryAfter),
            SlackThreadPageOutcome.ProviderRejected => SlackThreadReadResult.Fail(
                ProviderErrorCode(page.ErrorClass),
                ProviderErrorMessage(page.ErrorClass),
                providerError: page.ErrorClass),
            SlackThreadPageOutcome.InvalidResponse => SlackThreadReadResult.Fail(
                "invalid_provider_response",
                "Slack returned message or pagination facts Mohist could not read; the thread is not fully traversed."),
            _ => SlackThreadReadResult.Fail(
                "provider_unavailable",
                "Slack did not answer the thread read; the discussion was not read."),
        };
    }

    private static string TargetErrorCode(SlackThreadReadTargetOutcome outcome) => outcome switch
    {
        SlackThreadReadTargetOutcome.SessionNotFound => "session_not_found",
        SlackThreadReadTargetOutcome.NotBound => "not_bound",
        SlackThreadReadTargetOutcome.Conflicting => "binding_conflict",
        SlackThreadReadTargetOutcome.DirectMessage => "dm_not_supported",
        SlackThreadReadTargetOutcome.ManagerConversation => "manager_conversation",
        _ => "binding_conflict",
    };

    private static string ProviderErrorCode(string? errorClass) =>
        string.Equals(errorClass, "invalid_cursor", StringComparison.Ordinal)
            ? "cursor_rejected"
            : "provider_rejected";

    private static string ProviderErrorMessage(string? errorClass) => errorClass switch
    {
        "not_in_channel" => "The Bot is no longer in this channel, so Slack refused the thread read.",
        "channel_not_found" => "Slack no longer returns this channel for the Bot.",
        "thread_not_found" or "message_not_found" => "Slack no longer returns this thread.",
        "invalid_cursor" => "Slack rejected the continuation cursor; start a new read without --continuation.",
        "missing_scope" => "The installed Bot is missing the permission needed to read this thread.",
        "invalid_auth" or "token_revoked" or "account_inactive" =>
            "The Connection's Bot credential is no longer valid; reconnect the Slack adapter.",
        _ => $"Slack refused the thread read ({errorClass ?? "unknown_error"}).",
    };
}
