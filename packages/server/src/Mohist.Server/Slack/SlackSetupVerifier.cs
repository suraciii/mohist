using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;

namespace Mohist.Server.Slack;

public sealed record SlackSetupVerificationResult(bool Verified, string SetupProgress, string? Reason, IReadOnlyList<string> RequiredScopes);

public sealed record RotationCheckResult(
    bool Verified,
    string? Reason,
    string? ResolvedTeamId,
    string? ResolvedAppId,
    string? ResolvedBotUserId,
    string? VerifiedBotName,
    string? VerifiedBotIconUrl);

public sealed class SlackSetupVerifier
{
    private static readonly string[] RequiredScopes = ["chat:write", "users:read", "im:history"];
    private const string ScopeVerificationUnavailableReason = "Slack scope verification is unavailable because the scope-list API is unsupported. Confirm the required scopes are installed and retry verification.";
    private readonly ISlackApiClient _slack;
    private readonly ISecretStore _secrets;
    private readonly AgentConnectionStore _connections;
    private readonly TimeProvider _time;
    private readonly IOptions<SlackProviderOptions> _slackOptions;

    public SlackSetupVerifier(
        ISlackApiClient slack,
        ISecretStore secrets,
        AgentConnectionStore connections,
        TimeProvider time,
        IOptions<SlackProviderOptions> slackOptions)
    {
        _slack = slack;
        _secrets = secrets;
        _connections = connections;
        _time = time;
        _slackOptions = slackOptions;
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
        SlackPermissionsScopesListResponse grantedScopes;
        try
        {
            auth = await _slack.AuthTestAsync(tokenText, ct);
            if (!auth.Ok || string.IsNullOrWhiteSpace(auth.TeamId) || string.IsNullOrWhiteSpace(auth.UserId) || string.IsNullOrWhiteSpace(auth.BotId))
                return await FailAsync(projectId, connection, $"Slack rejected the Bot token: {auth.Error ?? "invalid_auth"}. Generate a new token.", ct);
            bot = await _slack.BotsInfoAsync(auth.BotId, tokenText, ct);
            grantedScopes = await GetGrantedScopesAsync(auth, tokenText, ct);
        }
        catch (HttpRequestException)
        {
            return await FailAsync(projectId, connection, "Slack could not be reached. Start mohist-slack and retry verification.", ct);
        }

        var reason = !bot.Ok || bot.Bot is null || string.IsNullOrWhiteSpace(bot.Bot.AppId) ? $"Slack could not resolve the configured App and Bot: {bot.Error ?? "bots.info did not return an App identity"}. Check the App and Bot installation." :
            !string.Equals(bot.Bot.Id, auth.BotId, StringComparison.Ordinal) ? "The App and Bot belong to different Slack installs. Reinstall the matching App and Bot." :
            ScopeFailureReason(grantedScopes);

        if (reason is not null)
            return await FailAsync(projectId, connection, reason, ct);

        try
        {
            await _connections.BindSlackIdentityAsync(projectId, connectionId, auth.TeamId, bot.Bot!.AppId!, auth.UserId, auth.User, ct);
        }
        catch (AgentConnectionValidationException ex)
        {
            return await FailAsync(projectId, connection, ex.Message, ct);
        }
        var verifiedBot = bot.Bot!;
        var setupProgress = connection.OwnerSlackUserId is null
            ? SetupProgressKind.ClaimOwner
            : SetupProgressKind.Complete;
        await _connections.UpdateAsync(projectId, connectionId, new HashSet<string>(StringComparer.Ordinal)
        {
            "setupProgress", "connectionHealth", "healthReason", "verifiedBotName", "verifiedBotIconUrl",
        }, setupProgress: setupProgress, connectionHealth: ConnectionHealthKind.Healthy,
            healthReason: null, verifiedBotName: verifiedBot.Name, verifiedBotIconUrl: verifiedBot.IconUrl, ct: ct);
        return new(true, setupProgress, null, RequiredScopes);
    }

    public async Task<RotationCheckResult> VerifyRotationAsync(
        string projectId,
        string connectionId,
        string appToken,
        string botToken,
        CancellationToken ct = default)
    {
        var connection = await _connections.GetAsync(projectId, connectionId, ct)
            ?? throw new InvalidOperationException("Connection was not found.");
        return await RunVerificationAsync(appToken, botToken, ct);
    }

    private async Task<RotationCheckResult> RunVerificationAsync(
        string appToken,
        string botToken,
        CancellationToken ct)
    {
        SlackAppsConnectionOpenResponse app;
        SlackAuthTestResponse auth;
        SlackBotInfoResponse bot;
        SlackPermissionsScopesListResponse grantedScopes;
        string appId;
        try
        {
            app = await _slack.AppsConnectionsOpenAsync(appToken, ct);
            if (!app.Ok || string.IsNullOrWhiteSpace(app.Url))
                return new(false, $"Slack rejected the App token: {app.Error ?? "invalid_auth"}. Generate a new token.", null, null, null, null, null);
            var resolvedAppId = AppIdFromAppToken(appToken) ?? AppIdFromSocketModeUrl(app.Url);
            if (resolvedAppId is null)
                return new(false, "Slack did not return an App identity for the App token. Generate a new App token.", null, null, null, null, null);
            appId = resolvedAppId;
            auth = await _slack.AuthTestAsync(botToken, ct);
            if (!auth.Ok || string.IsNullOrWhiteSpace(auth.TeamId) || string.IsNullOrWhiteSpace(auth.UserId) || string.IsNullOrWhiteSpace(auth.BotId))
                return new(false, $"Slack rejected the Bot token: {auth.Error ?? "invalid_auth"}. Generate a new token.", null, null, null, null, null);
            bot = await _slack.BotsInfoAsync(auth.BotId, botToken, ct);
            grantedScopes = await GetGrantedScopesAsync(auth, botToken, ct);
        }
        catch (HttpRequestException)
        {
            return new(false, "Slack could not be reached. Start mohist-slack and retry verification.", null, null, null, null, null);
        }

        var reason = !bot.Ok || bot.Bot is null || string.IsNullOrWhiteSpace(bot.Bot.AppId) ? $"Slack could not resolve the configured App and Bot: {bot.Error ?? "bots.info did not return an App identity"}. Check the App and Bot installation." :
            !string.Equals(bot.Bot.Id, auth.BotId, StringComparison.Ordinal) || !string.Equals(bot.Bot.AppId, appId, StringComparison.Ordinal) ? "The App token and Bot token belong to different Slack Apps. Reinstall the matching App and Bot." :
            ScopeFailureReason(grantedScopes);

        if (reason is not null)
            return new(false, reason, null, null, null, null, null);

        var verifiedBot = bot.Bot!;
        return new(true, null, auth.TeamId, verifiedBot.AppId, auth.UserId, verifiedBot.Name, verifiedBot.IconUrl);
    }

    private static string? AppIdFromSocketModeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0) continue;
            var key = Uri.UnescapeDataString(pair[..separator]);
            if (!string.Equals(key, "app_id", StringComparison.Ordinal)) continue;
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        return null;
    }

    private static string? AppIdFromAppToken(string token)
    {
        var parts = token.Split('-', StringSplitOptions.None);
        if (parts.Length < 3 || !string.Equals(parts[0], "xapp", StringComparison.Ordinal) || !string.Equals(parts[1], "1", StringComparison.Ordinal))
            return null;

        return string.IsNullOrWhiteSpace(parts[2]) ? null : parts[2];
    }

    private static string? ScopeFailureReason(SlackPermissionsScopesListResponse response)
    {
        if (!response.Ok && IsUnsupportedScopeListing(response.Error))
            return ScopeVerificationUnavailableReason;

        if (!response.Ok || response.Scopes is null)
            return $"Slack could not list the App's granted scopes: {response.Error ?? "apps.permissions.scopes.list failed"}. Reinstall the App and retry verification.";

        var granted = response.Scopes.Values.SelectMany(scopes => scopes);
        var missing = RequiredScopes.Where(scope => !granted.Contains(scope, StringComparer.Ordinal)).ToArray();
        return missing.Length == 0
            ? null
            : $"Slack is missing required scopes: {string.Join(", ", missing)}. Add the scopes and reinstall the App.";
    }

    private Task<SlackPermissionsScopesListResponse> GetGrantedScopesAsync(
        SlackAuthTestResponse auth,
        string botToken,
        CancellationToken ct)
    {
        if (auth.GrantedScopes is { } scopes)
        {
            return Task.FromResult(new SlackPermissionsScopesListResponse(
                true,
                null,
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["x-oauth-scopes"] = scopes.ToArray(),
                }));
        }

        return _slack.PermissionsScopesListAsync(botToken, ct);
    }

    private static bool IsUnsupportedScopeListing(string? error) =>
        string.Equals(error, "unknown_method", StringComparison.Ordinal)
        || string.Equals(error, "method_not_supported", StringComparison.Ordinal);

    public async Task<AgentConnection?> RecordAdapterHeartbeatAsync(string projectId, string connectionId, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var existing = await _connections.GetAsync(projectId, connectionId, ct);
        if (existing is null) return null;

        var retention = _slackOptions.Value.SlackEventRetentionWindow;
        var fields = new HashSet<string>(StringComparer.Ordinal) { "lastHeartbeatAt" };
        DateTimeOffset? offlineGapAt = null;
        if (existing.LastHeartbeatAt is { } previous && retention > TimeSpan.Zero && now - previous >= retention)
            offlineGapAt = now;

        if (offlineGapAt is not null) fields.Add("offlineGapAt");

        return await _connections.UpdateAsync(
            projectId,
            connectionId,
            fields,
            lastHeartbeatAt: now,
            offlineGapAt: offlineGapAt,
            ct: ct);
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
