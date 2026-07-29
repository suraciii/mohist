using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Security.Secrets;

namespace Mohist.Server.Slack;

public sealed record SlackSetupVerificationResult(bool Verified, string SetupProgress, string? Reason, IReadOnlyList<string> RequiredScopes);

public sealed class SlackSetupVerifier
{
    private static readonly string[] RequiredScopes = ["chat:write", "users:read", "im:history"];
    private readonly ISlackApiClient _slack;
    private readonly ISecretStore _secrets;
    private readonly AgentConnectionStore _connections;
    private readonly TimeProvider _time;

    public SlackSetupVerifier(ISlackApiClient slack, ISecretStore secrets, AgentConnectionStore connections, TimeProvider time)
    {
        _slack = slack;
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

        var tokenText = System.Text.Encoding.UTF8.GetString(token);
        SlackAuthTestResponse auth;
        SlackBotInfoResponse bot;
        try
        {
            auth = await _slack.AuthTestAsync(tokenText, ct);
            if (!auth.Ok || string.IsNullOrWhiteSpace(auth.TeamId) || string.IsNullOrWhiteSpace(auth.AppId) || string.IsNullOrWhiteSpace(auth.UserId))
                return await FailAsync(projectId, connection, $"Slack rejected the Bot token: {auth.Error ?? "invalid_auth"}. Generate a new token.", ct);
            bot = await _slack.BotsInfoAsync(auth.UserId, tokenText, ct);
        }
        catch (HttpRequestException)
        {
            return await FailAsync(projectId, connection, "Slack could not be reached. Start mohist-slack and retry verification.", ct);
        }

        var scopes = bot.Bot?.Scopes ?? [];
        var missing = RequiredScopes.Where(scope => !scopes.Contains(scope, StringComparer.Ordinal)).ToArray();
        var reason = !bot.Ok || bot.Bot is null ? $"Slack could not resolve the configured Bot: {bot.Error ?? "bots.info failed"}. Check the App and Bot installation." :
            !string.Equals(bot.Bot.AppId, auth.AppId, StringComparison.Ordinal) ? "The App and Bot belong to different Slack installs. Reinstall the matching App and Bot." :
            missing.Length > 0 ? $"Slack is missing required scopes: {string.Join(", ", missing)}. Add the scopes and reinstall the App." : null;

        if (reason is not null)
            return await FailAsync(projectId, connection, reason, ct);

        try
        {
            await _connections.BindSlackIdentityAsync(projectId, connectionId, auth.TeamId, auth.AppId, auth.UserId, auth.User, ct);
        }
        catch (AgentConnectionValidationException ex)
        {
            return await FailAsync(projectId, connection, ex.Message, ct);
        }
        await _connections.UpdateAsync(projectId, connectionId, new HashSet<string>(StringComparer.Ordinal) { "setupProgress", "connectionHealth", "healthReason" }, setupProgress: SetupProgressKind.ClaimOwner, connectionHealth: ConnectionHealthKind.Healthy, healthReason: null, ct: ct);
        return new(true, SetupProgressKind.ClaimOwner, null, RequiredScopes);
    }

    public async Task<AgentConnection?> RecordAdapterHeartbeatAsync(string projectId, string connectionId, CancellationToken ct = default)
    {
        return await _connections.UpdateAsync(projectId, connectionId, new HashSet<string>(StringComparer.Ordinal) { "lastHeartbeatAt" }, lastHeartbeatAt: _time.GetUtcNow(), ct: ct);
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
