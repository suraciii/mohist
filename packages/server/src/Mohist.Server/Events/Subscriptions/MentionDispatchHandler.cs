using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Events.Subscriptions;

/// <summary>
/// System handler that turns an <c>@&lt;agent&gt;</c> mention in an issue
/// comment into a one-shot Agent launch (issue-490). Subscribes to
/// <see cref="EventCatalog.ReverseDns.IssueCommentAdded"/>, scans the comment
/// body for mention tokens, resolves each to an active Agent in the comment's
/// project by name (case-insensitive), and launches each resolved Agent once
/// via the shared launcher's mention path
/// (<see cref="IAgentLauncher.LaunchMentionAsync"/> — workspace-optional, so a
/// mention fires regardless of the issue's workflow-run state).
///
/// <para>
/// <b>Loop prevention</b> (design D5): a comment whose declared
/// <c>author</c> matches the name of any active Agent in the project is never
/// scanned. Agent-authored comments therefore neither trigger other Agents
/// nor re-trigger themselves. Authorship is a declaration, not an
/// authentication: a human signing an Agent's name also produces a
/// non-triggering comment.
/// </para>
///
/// <para>
/// <b>Mute does not apply</b> (design D7): an explicit <c>@</c> mention is a
/// direct human directive. This handler does NOT consult
/// <c>WatchEntryStore</c>; a muted Agent on an issue is still launched when a
/// human explicitly <c>@</c>-mentions it. Mute continues to suppress only the
/// automatic paths enforced by <see cref="RoutingDispatchHandler"/>.
/// </para>
///
/// <para>
/// <b>Resolution failure is a no-op</b> (spec <i>Resolution failure is a
/// no-op</i>): <c>@</c>-ing a name with no matching active Agent (including a
/// name that matches only an archived Agent) starts nothing and emits a
/// structured log. The only externally observable signal of an unresolved
/// mention is that nothing happens.
/// </para>
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.IssueCommentAdded)]
public sealed class MentionDispatchHandler : ICloudEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MentionDispatchHandler> _log;

    public MentionDispatchHandler(IServiceScopeFactory scopeFactory, ILogger<MentionDispatchHandler> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public bool Filter(CloudEvent evt) => evt is not null;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) => DispatchAsync(evt, ct);

    private async Task DispatchAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!CloudEventLineage.TryReadIssueContext(evt.Extensions, out var issueContext))
        {
            _log.LogDebug(
                "Mention dispatch skipped: comment-added event {EventId} carries no project+issue lineage",
                evt.Id);
            return;
        }

        var payload = TryReadPayload(evt);
        if (payload is null)
        {
            _log.LogDebug(
                "Mention dispatch skipped: comment-added event {EventId} carries no payload",
                evt.Id);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var agentQuerier = services.GetRequiredService<AgentQuerier>();
        var launcher = services.GetRequiredService<IAgentLauncher>();
        var activeAgents = await agentQuerier.ListAsync(issueContext.ProjectId);

        if (IsAuthoredByActiveAgent(payload.Author, activeAgents))
        {
            _log.LogDebug(
                "Mention dispatch skipped: comment {CommentId} author '{Author}' matches an active Agent name in project {ProjectId} (loop prevention)",
                payload.CommentId,
                payload.Author,
                issueContext.ProjectId);
            return;
        }

        var tokens = MentionTokenParser.Parse(payload.Body);
        if (tokens.Count == 0)
        {
            _log.LogDebug(
                "Mention dispatch: comment {CommentId} on issue {IssueNumber} contains no @-mention tokens",
                payload.CommentId,
                issueContext.IssueNumber);
            return;
        }

        var launchedAgentIds = new HashSet<string>(StringComparer.Ordinal);
        var nameIndex = BuildActiveAgentNameIndex(activeAgents);

        foreach (var token in tokens)
        {
            if (!nameIndex.TryGetValue(token, out var agent))
            {
                _log.LogInformation(
                    "Mention dispatch: @-mention '{Token}' in comment {CommentId} on issue {IssueNumber} did not resolve to any active Agent in project {ProjectId}; no launch",
                    token,
                    payload.CommentId,
                    issueContext.IssueNumber,
                    issueContext.ProjectId);
                continue;
            }

            if (!launchedAgentIds.Add(agent.Id))
            {
                _log.LogDebug(
                    "Mention dispatch: @-mention '{Token}' in comment {CommentId} resolved to Agent {AgentId} already launched for this comment; skipping",
                    token,
                    payload.CommentId,
                    agent.Id);
                continue;
            }

            var launchContext = new AgentLaunchContext(
                ProjectId: issueContext.ProjectId,
                IssueNumber: issueContext.IssueNumber,
                EpicNumber: issueContext.EpicNumber,
                Repository: null,
                WorkspacePath: null,
                Title: null);

            await launcher.LaunchMentionAsync(
                agent,
                payload.Body,
                launchContext,
                payload.CommentId,
                evt.Id,
                ct);
            _log.LogInformation(
                "Mention dispatch: launched Agent {AgentId} ({AgentName}) for comment {CommentId} on issue {IssueNumber} in project {ProjectId}",
                agent.Id,
                agent.Name,
                payload.CommentId,
                issueContext.IssueNumber,
                issueContext.ProjectId);
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="author"/> case-insensitively
    /// matches the name of any active Agent in <paramref name="activeAgents"/>.
    /// Used for the declaration-based loop-prevention check (design D5).
    /// </summary>
    internal static bool IsAuthoredByActiveAgent(string author, IReadOnlyList<AgentInfo> activeAgents)
    {
        if (string.IsNullOrWhiteSpace(author))
            return false;

        foreach (var agent in activeAgents)
        {
            if (string.Equals(agent.Name, author, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Builds a case-insensitive name → Agent index over the project's active
    /// Agents. Two active Agents cannot share a name (enforced at Agent-create
    /// time via <c>EnsureNameAvailableAsync</c>, which is also case-insensitive
    /// after issue-490), so the index is unambiguous.
    /// </summary>
    internal static Dictionary<string, AgentInfo> BuildActiveAgentNameIndex(
        IReadOnlyList<AgentInfo> activeAgents,
        StringComparer comparer)
    {
        var index = new Dictionary<string, AgentInfo>(comparer);
        foreach (var agent in activeAgents)
        {
            index[agent.Name] = agent;
        }
        return index;
    }

    private static Dictionary<string, AgentInfo> BuildActiveAgentNameIndex(
        IReadOnlyList<AgentInfo> activeAgents) =>
        BuildActiveAgentNameIndex(activeAgents, StringComparer.OrdinalIgnoreCase);

    private static CommentAddedPayload? TryReadPayload(CloudEvent evt)
    {
        if (evt.Data is not { ValueKind: JsonValueKind.Object } data)
            return null;

        string? commentId = null;
        string? author = null;
        string? body = null;
        if (data.TryGetProperty("commentId", out var commentIdElement)
            && commentIdElement.ValueKind == JsonValueKind.String)
        {
            commentId = commentIdElement.GetString();
        }
        if (data.TryGetProperty("author", out var authorElement)
            && authorElement.ValueKind == JsonValueKind.String)
        {
            author = authorElement.GetString();
        }
        if (data.TryGetProperty("body", out var bodyElement)
            && bodyElement.ValueKind == JsonValueKind.String)
        {
            body = bodyElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(commentId) || author is null || body is null)
            return null;

        return new CommentAddedPayload(commentId, author, body);
    }

    private sealed record CommentAddedPayload(string CommentId, string Author, string Body);
}
