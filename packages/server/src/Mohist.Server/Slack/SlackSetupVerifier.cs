using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Slack;

public sealed record SlackSetupVerificationResult(bool Verified, string SetupProgress, string? Reason, IReadOnlyList<string> RequiredScopes);

public sealed class SlackSetupVerifier
{
    private static readonly string[] RequiredScopes = ["chat:write", "users:read", "im:history"];
    private readonly ISlackBotIdentityVerificationPort _identity;
    private readonly ISecretStore _secrets;
    private readonly AgentConnectionStore _connections;
    private readonly TimeProvider _time;

    public SlackSetupVerifier(
        ISlackBotIdentityVerificationPort identity,
        ISecretStore secrets,
        AgentConnectionStore connections,
        TimeProvider time)
    {
        _identity = identity;
        _secrets = secrets;
        _connections = connections;
        _time = time;
    }

    public async Task<SlackSetupVerificationResult> VerifyAsync(string projectId, string connectionId, CancellationToken ct = default)
    {
        var connection = await _connections.GetAsync(projectId, connectionId, ct)
            ?? throw new InvalidOperationException("Connection was not found.");
        var token = await _secrets.LoadAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.BotToken), ct);
        if (token is null || token.Length == 0)
            return await FailAsync(projectId, connection, "Save a Bot token before verifying Slack setup.", ct);

        var verified = await _identity.VerifyAsync(new(System.Text.Encoding.UTF8.GetString(token)), ct);
        if (!verified.Verified
            || string.IsNullOrWhiteSpace(verified.WorkspaceTeamId)
            || string.IsNullOrWhiteSpace(verified.BotUserId)
            || string.IsNullOrWhiteSpace(verified.AppId))
            return await FailAsync(projectId, connection, "Slack rejected the Bot token. Generate a new token.", ct);

        var missing = RequiredScopes.Where(scope => verified.GrantedScopes is null || !verified.GrantedScopes.Contains(scope)).ToArray();
        if (missing.Length > 0)
            return await FailAsync(projectId, connection, $"Slack is missing required scopes: {string.Join(", ", missing)}. Add the scopes and reinstall the App.", ct);

        try
        {
            await _connections.BindSlackIdentityAsync(projectId, connectionId, verified.WorkspaceTeamId, verified.AppId, verified.BotUserId, null, ct);
        }
        catch (AgentConnectionValidationException ex)
        {
            return await FailAsync(projectId, connection, ex.Message, ct);
        }
        var setupProgress = connection.OwnerSlackUserId is null ? SetupProgressKind.ClaimOwner : SetupProgressKind.Complete;
        await _connections.UpdateAsync(projectId, connectionId, new HashSet<string>(StringComparer.Ordinal)
        {
            "setupProgress", "connectionHealth", "healthReason",
        }, setupProgress: setupProgress, connectionHealth: ConnectionHealthKind.Healthy, healthReason: null, ct: ct);
        return new(true, setupProgress, null, RequiredScopes);
    }

    public bool IsAdapterOnline(AgentConnection connection, TimeSpan freshness = default)
    {
        freshness = freshness == default ? TimeSpan.FromMinutes(2) : freshness;
        return connection.LastHeartbeatAt is { } heartbeat && _time.GetUtcNow() - heartbeat <= freshness;
    }

    private async Task<SlackSetupVerificationResult> FailAsync(string projectId, AgentConnection connection, string reason, CancellationToken ct)
    {
        await _connections.UpdateAsync(projectId, connection.Id, new HashSet<string>(StringComparer.Ordinal) { "setupProgress", "connectionHealth", "healthReason" }, setupProgress: SetupProgressKind.FixSlackSetup, connectionHealth: ConnectionHealthKind.Unhealthy, healthReason: reason, ct: ct);
        return new(false, SetupProgressKind.FixSlackSetup, reason, RequiredScopes);
    }
}
