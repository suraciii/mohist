using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Slack.Services;

public enum SlackThreadReadTargetOutcome
{
    Resolved,
    SessionNotFound,
    NotBound,
    Conflicting,
    DirectMessage,
    ManagerConversation,
}

/// <summary>
/// The channel thread a Session is bound to. Every field is resolved from
/// recorded facts; the caller selects the Session and nothing else.
/// </summary>
public sealed record SlackThreadReadTarget(
    string ProjectId,
    string ConnectionId,
    string WorkspaceTeamId,
    string ChannelId,
    string RootMessageId);

public sealed record SlackThreadReadTargetResult(
    SlackThreadReadTargetOutcome Outcome,
    SlackThreadReadTarget? Target = null,
    string? Reason = null);

/// <summary>
/// Resolves the channel thread of one AgentSession from recorded Slack input
/// provenance, reconciled with the durable thread mapping. The resolver is
/// deliberately fail-closed: a Session outside the resolved Project, a Session
/// with no Slack association, more than one distinct association, a direct
/// message, and the Workspace Manager conversation all refuse the read instead
/// of guessing a thread.
/// </summary>
public sealed class SlackThreadReadBindingResolver(
    IAgentSessionStore sessions,
    SlackThreadSessionMappingStore threadMappings,
    SlackDmSessionMappingStore dmMappings) : IScopedService
{
    public async Task<SlackThreadReadTargetResult> ResolveAsync(
        string projectId,
        string sessionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = await sessions.LoadAsync(sessionId);
        if (session is null)
            return NotFound(sessionId);

        // A Session id is a resource selector, not a credential: the Project in
        // the route must own the Session before any provider call.
        if (!string.Equals(
                session.Metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId),
                projectId,
                StringComparison.Ordinal))
        {
            return NotFound(sessionId);
        }

        var provenance = (session.Status.Inputs ?? [])
            .Select(input => input.Provenance)
            .Where(record => record is not null
                && string.Equals(record.ProviderKind, "slack", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(record.ConnectionId))
            .Select(record => record!)
            .ToList();
        if (provenance.Count == 0)
        {
            return new SlackThreadReadTargetResult(
                SlackThreadReadTargetOutcome.NotBound,
                Reason: $"AgentSession '{sessionId}' is not associated with a Slack channel thread.");
        }

        if (provenance.Any(record => string.Equals(
                record.OriginMarker, AgentOriginMarkers.SlackManager, StringComparison.Ordinal)))
        {
            return new SlackThreadReadTargetResult(
                SlackThreadReadTargetOutcome.ManagerConversation,
                Reason: "The Workspace Manager conversation is not a readable channel thread.");
        }

        var targets = provenance
            .Select(record => new SlackThreadReadTarget(
                projectId,
                record.ConnectionId!,
                record.WorkspaceId,
                record.ConversationId,
                record.BoundThreadRootMessageId ?? record.ThreadId ?? record.MessageId))
            .Distinct()
            .ToList();
        if (targets.Count != 1)
        {
            return new SlackThreadReadTargetResult(
                SlackThreadReadTargetOutcome.Conflicting,
                Reason: $"AgentSession '{sessionId}' records more than one Slack conversation association.");
        }

        var target = targets[0];
        if (string.IsNullOrWhiteSpace(target.WorkspaceTeamId)
            || string.IsNullOrWhiteSpace(target.ChannelId)
            || string.IsNullOrWhiteSpace(target.RootMessageId))
        {
            return new SlackThreadReadTargetResult(
                SlackThreadReadTargetOutcome.NotBound,
                Reason: $"AgentSession '{sessionId}' has incomplete Slack conversation provenance.");
        }

        // The DM mapping is keyed by conversation and outlives a `new task`
        // swap, so a row means this conversation is a direct message even when
        // the recorded Session has since changed.
        if (await dmMappings.GetCurrentSessionIdAsync(
                projectId, target.ConnectionId, target.WorkspaceTeamId, target.ChannelId, ct) is not null)
        {
            return new SlackThreadReadTargetResult(
                SlackThreadReadTargetOutcome.DirectMessage,
                Reason: "Direct message history is not readable through the channel-thread read.");
        }

        var mappedSessionId = await threadMappings.GetSessionIdAsync(
            projectId,
            target.WorkspaceTeamId,
            target.ConnectionId,
            target.ChannelId,
            target.RootMessageId,
            ct);
        if (mappedSessionId is not null && !string.Equals(mappedSessionId, sessionId, StringComparison.Ordinal))
        {
            return new SlackThreadReadTargetResult(
                SlackThreadReadTargetOutcome.Conflicting,
                Reason: $"Slack thread '{target.ChannelId}/{target.RootMessageId}' is bound to a different Session.");
        }

        return new SlackThreadReadTargetResult(SlackThreadReadTargetOutcome.Resolved, target);
    }

    private static SlackThreadReadTargetResult NotFound(string sessionId) =>
        new(
            SlackThreadReadTargetOutcome.SessionNotFound,
            Reason: $"AgentSession '{sessionId}' was not found in this Project.");
}
