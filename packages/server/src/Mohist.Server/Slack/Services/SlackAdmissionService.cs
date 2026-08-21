using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Slack;

namespace Mohist.Server.Slack.Services;

public static class SlackIngressResponseOwners
{
    public const string None = "none";
    public const string Server = "server";
    public const string Adapter = "adapter";
}

public static class SlackAdmissionMessages
{
    public const string AgentNotReady =
        "The Agent is not ready to accept new work. Please contact the Connection owner or complete setup through the normal owner workflow.";

    public const string ConnectionUnavailable =
        "This Connection is temporarily unavailable for new work. Please retry shortly or contact the Connection owner.";

    public const string Backpressured =
        "This Slack Connection is temporarily busy. Please retry shortly.";
}

public sealed record SlackAdmissionDecision(
    bool Admitted,
    string Kind,
    string? Reason,
    string ResponseOwner);

/// <summary>
/// Decides whether Slack new work may be admitted and owns the durable
/// response boundary when it cannot. Follow-ups do not use this service.
/// </summary>
public sealed class SlackAdmissionService : IScopedService
{
    private const string DispatchPrefix = "slack-admission-nudge:";
    private readonly AgentReadinessService _readiness;
    private readonly SlackOutboxStore _outbox;
    private readonly SlackSetupVerifier _setupVerifier;

    public SlackAdmissionService(
        AgentReadinessService readiness,
        SlackOutboxStore outbox,
        SlackSetupVerifier setupVerifier)
    {
        _readiness = readiness;
        _outbox = outbox;
        _setupVerifier = setupVerifier;
    }

    public async Task<SlackAdmissionDecision> AdmitNewWorkAsync(
        string projectId,
        AgentConnection connection,
        AgentInfo agent,
        SlackMessageIdentity identity,
        string? threadTs,
        CancellationToken ct = default)
    {
        var executability = await _readiness.GetAsync(projectId, agent, ct);

        // Backpressure is the one deliberately non-durable response path.
        // Check it after obtaining the canonical Agent result so the new-work
        // decision never depends on the legacy readiness projection.
        if (IsBackpressured(connection))
            return AdapterOwnedBackpressure();

        if (AgentExecutabilityStates.IsBlocked(executability.State))
        {
            return await PersistNudgeOrUseBackpressureAsync(
                projectId,
                connection,
                identity,
                threadTs,
                kind: executability.State == AgentExecutabilityStates.NotConfigured
                    ? "agent_not_configured"
                    : "agent_not_executable",
                text: SlackAdmissionMessages.AgentNotReady,
                ct);
        }

        var connectionDiagnostic = ConnectionDiagnostic.Compute(
            connection,
            new DiagnosticInputs(
                AdapterOnline: _setupVerifier.IsAdapterOnline(connection),
                AgentExecutability: executability));
        var connectionBlockKind = ConnectionBlockKind(connectionDiagnostic.PrimaryState)
            ?? (connection.ConnectionHealth == ConnectionHealthKind.Unhealthy
                || !string.Equals(connection.SetupProgress, SetupProgressKind.Complete, StringComparison.Ordinal)
                    ? "connection_unavailable"
                    : null);
        if (connectionBlockKind is not null)
        {
            return await PersistNudgeOrUseBackpressureAsync(
                projectId,
                connection,
                identity,
                threadTs,
                connectionBlockKind,
                SlackAdmissionMessages.ConnectionUnavailable,
                ct);
        }

        return new SlackAdmissionDecision(
            Admitted: true,
            Kind: "accepted",
            Reason: null,
            ResponseOwner: SlackIngressResponseOwners.None);
    }

    public static string DispatchRef(AgentConnection connection, SlackMessageIdentity identity)
    {
        var canonical = string.Join('\n', connection.Id, identity.WorkspaceTeamId, identity.ConversationId, identity.MessageTs);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return DispatchPrefix + digest;
    }

    private async Task<SlackAdmissionDecision> PersistNudgeOrUseBackpressureAsync(
        string projectId,
        AgentConnection connection,
        SlackMessageIdentity identity,
        string? threadTs,
        string kind,
        string text,
        CancellationToken ct)
    {
        var dispatchRef = DispatchRef(connection, identity);
        try
        {
            await _outbox.EnqueueRequiredAsync(new SlackOutboxDraft(
                projectId,
                connection.Id,
                identity.WorkspaceTeamId,
                identity.ConversationId,
                SlackOutboxKinds.UserAction,
                dispatchRef,
                JsonSerializer.Serialize(new SlackDeliveryPayload(
                    SlackDeliveryOperations.PostMessage,
                    text,
                    ClientMessageId: dispatchRef)),
                threadTs), ct);
        }
        catch (SlackOutboxCapacityExceededException)
        {
            return AdapterOwnedBackpressure();
        }

        return new SlackAdmissionDecision(
            Admitted: false,
            Kind: kind,
            Reason: text,
            ResponseOwner: SlackIngressResponseOwners.Server);
    }

    private static SlackAdmissionDecision AdapterOwnedBackpressure() => new(
        Admitted: false,
        Kind: "backpressured",
        Reason: SlackAdmissionMessages.Backpressured,
        ResponseOwner: SlackIngressResponseOwners.Adapter);

    private static bool IsBackpressured(AgentConnection connection) =>
        connection.ConnectionHealth == ConnectionHealthKind.Degraded
        && SlackConnectionBackpressureReasons.IsBackpressureReason(connection.HealthReason);

    private static string? ConnectionBlockKind(string primaryState) => primaryState switch
    {
        ConnectionDiagnosticState.SetupIncomplete => "connection_unavailable",
        ConnectionDiagnosticState.CredentialsInvalid => "connection_unavailable",
        ConnectionDiagnosticState.ServiceOffline => "connection_unavailable",
        _ => null,
    };
}
