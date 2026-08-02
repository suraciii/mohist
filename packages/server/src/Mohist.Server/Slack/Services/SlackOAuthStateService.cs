using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

public sealed class SlackOAuthStateService : IScopedService
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public SlackOAuthStateService(IDbContextFactory<MohistDbContext> dbFactory, TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task<SlackOAuthStateIssued> IssueAsync(
        string childAppId,
        string workspaceTeamId,
        string appId,
        TimeSpan? lifetime = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceTeamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var now = _timeProvider.GetUtcNow();
        var expiresAt = now.Add(lifetime ?? DefaultLifetime);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var childExists = await db.ManagedSlackChildApps.AnyAsync(child =>
            child.Id == childAppId
            && child.WorkspaceTeamId == workspaceTeamId
            && child.AppId == appId
            && child.DeletedAt == null, ct);
        if (!childExists) throw new InvalidOperationException("The OAuth state target does not match a managed Child App.");
        db.SlackOAuthStates.Add(new SlackOAuthStateRow
        {
            Id = $"oauth_state_{Guid.NewGuid():N}",
            ChildAppId = childAppId,
            WorkspaceTeamId = workspaceTeamId,
            AppId = appId,
            StateHash = Hash(state),
            ExpiresAt = expiresAt,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return new(state, expiresAt);
    }

    public async Task<SlackOAuthStateValidation> ConsumeAsync(
        string state,
        string childAppId,
        string workspaceTeamId,
        string appId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state)) return SlackOAuthStateValidation.Invalid;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var hash = Hash(state);
        var row = await db.SlackOAuthStates.AsNoTracking().SingleOrDefaultAsync(item => item.StateHash == hash, ct);
        if (row is null) return SlackOAuthStateValidation.Invalid;
        if (row.ChildAppId != childAppId || row.WorkspaceTeamId != workspaceTeamId || row.AppId != appId)
            return SlackOAuthStateValidation.Mismatch;

        var now = _timeProvider.GetUtcNow();
        if (row.ConsumedAt is null && row.ExpiresAt <= now)
        {
            var expired = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"SlackOAuthStates\" SET \"ConsumedAt\" = {now}, \"Outcome\" = {SlackOAuthStateOutcome.Expired} WHERE \"StateHash\" = {hash} AND \"ConsumedAt\" IS NULL AND \"ExpiresAt\" <= {now}", ct);
            if (expired == 1) return SlackOAuthStateValidation.Expired;
        }
        else if (row.ConsumedAt is null)
        {
            var accepted = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"SlackOAuthStates\" SET \"ConsumedAt\" = {now}, \"Outcome\" = {SlackOAuthStateOutcome.Accepted} WHERE \"StateHash\" = {hash} AND \"ConsumedAt\" IS NULL AND \"ExpiresAt\" > {now}", ct);
            if (accepted == 1) return SlackOAuthStateValidation.Accepted;
        }

        var afterRace = await db.SlackOAuthStates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.StateHash == hash, ct);
        return afterRace?.Outcome == SlackOAuthStateOutcome.Accepted
            ? SlackOAuthStateValidation.ReplayAccepted
            : afterRace?.Outcome == SlackOAuthStateOutcome.Expired
                ? SlackOAuthStateValidation.ReplayRejected
                : SlackOAuthStateValidation.Invalid;
    }

    internal static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed record SlackOAuthStateIssued(string State, DateTimeOffset ExpiresAt);

public enum SlackOAuthStateValidation
{
    Invalid,
    Mismatch,
    Expired,
    Accepted,
    ReplayAccepted,
    ReplayRejected,
}

public static class SlackOAuthStateOutcome
{
    public const string Accepted = "accepted";
    public const string Expired = "expired";
}

public interface ISlackOAuthCredentialSink
{
    Task<string> StoreBotTokenAsync(string childAppId, string botToken, CancellationToken ct = default);
}

public sealed class UnavailableSlackOAuthCredentialSink : ISlackOAuthCredentialSink, IScopedService
{
    public Task<string> StoreBotTokenAsync(string childAppId, string botToken, CancellationToken ct = default) =>
        throw new NotSupportedException("Slack OAuth credential storage is not connected in this slice.");
}

public sealed class FakeSlackOAuthCredentialSink : ISlackOAuthCredentialSink
{
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Tokens => _tokens;

    public Task<string> StoreBotTokenAsync(string childAppId, string botToken, CancellationToken ct = default)
    {
        if (!botToken.StartsWith("xoxb-", StringComparison.Ordinal))
            throw new InvalidOperationException("Only Slack Bot tokens can be stored.");
        var reference = $"slack-child:{childAppId}:bot-token";
        _tokens[reference] = botToken;
        return Task.FromResult(reference);
    }
}

public sealed class SlackOAuthAuthorizationService : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly SlackOAuthStateService _states;
    private readonly ISlackOAuthCredentialSink _credentials;
    private readonly TimeProvider _timeProvider;

    public SlackOAuthAuthorizationService(
        IDbContextFactory<MohistDbContext> dbFactory,
        SlackOAuthStateService states,
        ISlackOAuthCredentialSink credentials,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _states = states;
        _credentials = credentials;
        _timeProvider = timeProvider;
    }

    public async Task<SlackOAuthAuthorizationResult> RecordProgressAsync(
        string childAppId,
        string authorization,
        CancellationToken ct = default)
    {
        if (authorization is not SlackAuthorizationState.AwaitingUser
            and not SlackAuthorizationState.PendingAdmin
            and not SlackAuthorizationState.ExpiredOrCancelled)
            throw new ArgumentException("Unsupported OAuth authorization progress.", nameof(authorization));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var changed = await db.ManagedSlackChildApps
            .Where(item => item.Id == childAppId && item.DeletedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Authorization, authorization)
                .SetProperty(item => item.UpdatedAt, _timeProvider.GetUtcNow()), ct);
        return changed == 1
            ? SlackOAuthAuthorizationResult.Progressed(authorization)
            : SlackOAuthAuthorizationResult.RecoveryRequired;
    }

    public async Task<SlackOAuthAuthorizationResult> AuthorizeAsync(
        string state,
        string childAppId,
        string workspaceTeamId,
        string appId,
        string botUserId,
        string botToken,
        CancellationToken ct = default)
    {
        if (!botToken.StartsWith("xoxb-", StringComparison.Ordinal))
            return SlackOAuthAuthorizationResult.Rejected("invalid_bot_token");
        if (string.IsNullOrWhiteSpace(botUserId))
            return SlackOAuthAuthorizationResult.Rejected("bot_identity_required");

        var validation = await _states.ConsumeAsync(state, childAppId, workspaceTeamId, appId, ct);
        if (validation is SlackOAuthStateValidation.Mismatch or SlackOAuthStateValidation.Invalid
            or SlackOAuthStateValidation.Expired or SlackOAuthStateValidation.ReplayRejected)
            return SlackOAuthAuthorizationResult.Rejected(validation.ToString().ToLowerInvariant());
        if (validation == SlackOAuthStateValidation.ReplayAccepted)
        {
            await using var replayDb = await _dbFactory.CreateDbContextAsync(ct);
            var replayChild = await replayDb.ManagedSlackChildApps.AsNoTracking().SingleOrDefaultAsync(item => item.Id == childAppId, ct);
            return replayChild?.Authorization == SlackAuthorizationState.Authorized
                ? SlackOAuthAuthorizationResult.AlreadyApplied(replayChild.BotUserId)
                : SlackOAuthAuthorizationResult.RecoveryRequired;
        }

        var tokenRef = await _credentials.StoreBotTokenAsync(childAppId, botToken, ct);
        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var changed = await db.ManagedSlackChildApps
            .Where(item => item.Id == childAppId
                && item.WorkspaceTeamId == workspaceTeamId
                && item.AppId == appId
                && item.Authorization != SlackAuthorizationState.Authorized)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.BotUserId, botUserId)
                .SetProperty(item => item.BotTokenRef, tokenRef)
                .SetProperty(item => item.Authorization, SlackAuthorizationState.Authorized)
                .SetProperty(item => item.AuthorizationAttemptId, childAppId)
                .SetProperty(item => item.AuthorizedAt, now)
                .SetProperty(item => item.UpdatedAt, now), ct);
        return changed == 1
            ? SlackOAuthAuthorizationResult.Accepted(botUserId)
            : SlackOAuthAuthorizationResult.RecoveryRequired;
    }
}

public sealed record SlackOAuthAuthorizationResult(
    SlackOAuthAuthorizationStatus Status,
    string? BotUserId = null,
    string? ErrorClass = null)
{
    public static SlackOAuthAuthorizationResult Accepted(string botUserId) => new(SlackOAuthAuthorizationStatus.Accepted, botUserId);
    public static SlackOAuthAuthorizationResult AlreadyApplied(string botUserId) => new(SlackOAuthAuthorizationStatus.AlreadyApplied, botUserId);
    public static SlackOAuthAuthorizationResult Progressed(string authorization) => new(SlackOAuthAuthorizationStatus.Progressed, ErrorClass: authorization);
    public static SlackOAuthAuthorizationResult Rejected(string errorClass) => new(SlackOAuthAuthorizationStatus.Rejected, ErrorClass: errorClass);
    public static SlackOAuthAuthorizationResult RecoveryRequired { get; } = new(SlackOAuthAuthorizationStatus.RecoveryRequired);
}

public enum SlackOAuthAuthorizationStatus
{
    Accepted,
    AlreadyApplied,
    Progressed,
    Rejected,
    RecoveryRequired,
}
