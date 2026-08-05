using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
        string agentAppId,
        string workspaceTeamId,
        string appId,
        TimeSpan? lifetime = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceTeamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var stateHash = Hash(state);
        var now = _timeProvider.GetUtcNow();
        var expiresAt = now.Add(lifetime ?? DefaultLifetime);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var childExists = await db.ManagedSlackAgentApps.AnyAsync(agentApp =>
            agentApp.Id == agentAppId
            && agentApp.WorkspaceTeamId == workspaceTeamId
            && agentApp.AppId == appId
            && agentApp.DeletedAt == null, ct);
        if (!childExists) throw new InvalidOperationException("The OAuth state target does not match a managed Agent App.");

        var attemptId = $"oauth_attempt_{Guid.NewGuid():N}";
        db.SlackOAuthAttempts.Add(new SlackOAuthAttemptRow
        {
            Id = attemptId,
            AgentAppId = agentAppId,
            WorkspaceTeamId = workspaceTeamId,
            AppId = appId,
            StateHash = stateHash,
            Status = SlackOAuthAttemptStatus.Issued,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SlackOAuthStates.Add(new SlackOAuthStateRow
        {
            Id = $"oauth_state_{Guid.NewGuid():N}",
            AgentAppId = agentAppId,
            WorkspaceTeamId = workspaceTeamId,
            AppId = appId,
            StateHash = stateHash,
            AuthorizationAttemptId = attemptId,
            ExpiresAt = expiresAt,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return new(state, expiresAt, attemptId);
    }

    public async Task<SlackOAuthStateValidation> ConsumeAsync(
        string state,
        string agentAppId,
        string workspaceTeamId,
        string appId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state)) return SlackOAuthStateValidation.Invalid;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var hash = Hash(state);
        var row = await db.SlackOAuthStates.AsNoTracking().SingleOrDefaultAsync(item => item.StateHash == hash, ct);
        if (row is null) return SlackOAuthStateValidation.Invalid;
        if (row.AgentAppId != agentAppId || row.WorkspaceTeamId != workspaceTeamId || row.AppId != appId)
            return SlackOAuthStateValidation.Mismatch;

        var now = _timeProvider.GetUtcNow();
        if (row.ConsumedAt is null && row.ExpiresAt <= now)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var expired = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "SlackOAuthStates"
                SET "ConsumedAt" = {now}, "Outcome" = {SlackOAuthStateOutcome.Expired}
                WHERE "StateHash" = {hash}
                  AND "ConsumedAt" IS NULL
                  AND "ExpiresAt" <= {now};
                """, ct);
            if (expired == 1)
            {
                await db.SlackOAuthAttempts
                    .Where(item => item.Id == row.AuthorizationAttemptId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Status, SlackOAuthAttemptStatus.Expired)
                        .SetProperty(item => item.UpdatedAt, now), ct);
                await transaction.CommitAsync(ct);
                return SlackOAuthStateValidation.Expired;
            }
            await transaction.RollbackAsync(ct);
        }
        else if (row.ConsumedAt is null)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var accepted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "SlackOAuthStates"
                SET "ConsumedAt" = {now}, "Outcome" = {SlackOAuthStateOutcome.Accepted}
                WHERE "StateHash" = {hash}
                  AND "ConsumedAt" IS NULL
                  AND "ExpiresAt" > {now};
                """, ct);
            if (accepted == 1)
            {
                await db.SlackOAuthAttempts
                    .Where(item => item.Id == row.AuthorizationAttemptId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Status, SlackOAuthAttemptStatus.Consumed)
                        .SetProperty(item => item.ConsumedAt, (DateTimeOffset?)now)
                        .SetProperty(item => item.UpdatedAt, now), ct);
                await transaction.CommitAsync(ct);
                return SlackOAuthStateValidation.Accepted;
            }
            await transaction.RollbackAsync(ct);
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

public sealed record SlackOAuthStateIssued(string State, DateTimeOffset ExpiresAt, string AuthorizationAttemptId);

public enum SlackOAuthStateValidation
{
    Invalid,
    Mismatch,
    Expired,
    Accepted,
    ReplayAccepted,
    ReplayRejected,
}

public static class SlackSecretRedactor
{
    private static readonly Regex TokenPattern = new(@"(?i)(?:xoxb|xapp|xoxe|xoxp|xoxs)-[A-Za-z0-9._-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string value) => TokenPattern.Replace(value, "[REDACTED]");
}

public interface ISlackOAuthCredentialSink
{
    Task<string> GetOrStoreBotTokenAsync(string agentAppId, string authorizationAttemptId, string botToken, CancellationToken ct = default);
}

public sealed class UnavailableSlackOAuthCredentialSink : ISlackOAuthCredentialSink, IScopedService
{
    public Task<string> GetOrStoreBotTokenAsync(string agentAppId, string authorizationAttemptId, string botToken, CancellationToken ct = default) =>
        throw new NotSupportedException("Slack OAuth credential storage is not connected in this slice.");
}

public sealed class FakeSlackOAuthCredentialSink : ISlackOAuthCredentialSink
{
    private readonly Dictionary<string, string> _referencesByAttempt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokensByReference = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Tokens => _tokensByReference;

    public Task<string> GetOrStoreBotTokenAsync(string agentAppId, string authorizationAttemptId, string botToken, CancellationToken ct = default)
    {
        if (!botToken.StartsWith("xoxb-", StringComparison.Ordinal))
            throw new InvalidOperationException("Only Slack Bot tokens can be stored.");
        if (_referencesByAttempt.TryGetValue(authorizationAttemptId, out var existingReference))
            return Task.FromResult(existingReference);

        var reference = $"slack-oauth-attempt:{authorizationAttemptId}:bot-token";
        _referencesByAttempt.Add(authorizationAttemptId, reference);
        _tokensByReference.Add(reference, botToken);
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
        string agentAppId,
        string authorization,
        CancellationToken ct = default)
    {
        if (authorization is not SlackAuthorizationState.AwaitingUser
            and not SlackAuthorizationState.PendingAdmin
            and not SlackAuthorizationState.ExpiredOrCancelled)
            throw new ArgumentException("Unsupported OAuth authorization progress.", nameof(authorization));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var current = await db.ManagedSlackAgentApps.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == agentAppId && item.DeletedAt == null, ct);
        if (current is null)
            return SlackOAuthAuthorizationResult.RecoveryRequired;
        SlackStateTransitions.RequireAuthorizationTransition(current.Authorization, authorization);
        var changed = await db.ManagedSlackAgentApps
            .Where(item => item.Id == agentAppId
                && item.DeletedAt == null
                && item.Authorization == current.Authorization)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Authorization, authorization)
                .SetProperty(item => item.UpdatedAt, _timeProvider.GetUtcNow()), ct);
        return changed == 1
            ? SlackOAuthAuthorizationResult.Progressed(authorization)
            : SlackOAuthAuthorizationResult.RecoveryRequired;
    }

    public async Task<SlackOAuthAuthorizationResult> AuthorizeAsync(
        string state,
        string agentAppId,
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

        var validation = await _states.ConsumeAsync(state, agentAppId, workspaceTeamId, appId, ct);
        if (validation is SlackOAuthStateValidation.Mismatch or SlackOAuthStateValidation.Invalid
            or SlackOAuthStateValidation.Expired or SlackOAuthStateValidation.ReplayRejected)
            return SlackOAuthAuthorizationResult.Rejected(validation.ToString().ToLowerInvariant());

        var stateHash = SlackOAuthStateService.Hash(state);
        await using var lookup = await _dbFactory.CreateDbContextAsync(ct);
        var stateRow = await lookup.SlackOAuthStates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.StateHash == stateHash
                && item.AgentAppId == agentAppId
                && item.WorkspaceTeamId == workspaceTeamId
                && item.AppId == appId, ct);
        if (stateRow?.AuthorizationAttemptId is null)
            return SlackOAuthAuthorizationResult.RecoveryRequired;
        var attempt = await lookup.SlackOAuthAttempts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == stateRow.AuthorizationAttemptId, ct);
        if (attempt is null)
            return SlackOAuthAuthorizationResult.RecoveryRequired;

        if (validation == SlackOAuthStateValidation.ReplayAccepted && attempt.Status == SlackOAuthAttemptStatus.Applied)
            return SlackOAuthAuthorizationResult.AlreadyApplied(attempt.BotUserId);
        if (attempt.Status == SlackOAuthAttemptStatus.Expired)
            return SlackOAuthAuthorizationResult.Rejected("expired");
        if (!string.IsNullOrWhiteSpace(attempt.BotUserId)
            && !string.Equals(attempt.BotUserId, botUserId, StringComparison.Ordinal))
            return SlackOAuthAuthorizationResult.Rejected("bot_identity_mismatch");

        var hasStoredToken = attempt.Status is SlackOAuthAttemptStatus.SecretStored
            or SlackOAuthAttemptStatus.Applied
            or SlackOAuthAttemptStatus.RecoveryRequired;
        var tokenRef = hasStoredToken && !string.IsNullOrWhiteSpace(attempt.BotTokenRef)
            ? attempt.BotTokenRef
            : null;
        if (tokenRef is null)
        {
            try
            {
                tokenRef = await _credentials.GetOrStoreBotTokenAsync(agentAppId, attempt.Id, botToken, ct);
            }
            catch
            {
                await MarkAttemptRecoveryRequiredAsync(attempt.Id, "secret_store_failure", ct);
                return SlackOAuthAuthorizationResult.RecoveryRequired;
            }

            try
            {
                await MarkSecretStoredAsync(attempt.Id, botUserId, tokenRef, ct);
            }
            catch
            {
                await MarkAttemptRecoveryRequiredAsync(attempt.Id, "secret_reference_persist_failure", ct);
                return SlackOAuthAuthorizationResult.RecoveryRequired;
            }
        }

        try
        {
            return await ApplyAuthorizationAsync(attempt.Id, agentAppId, workspaceTeamId, appId, botUserId, tokenRef, ct);
        }
        catch
        {
            await MarkAttemptRecoveryRequiredAsync(attempt.Id, "apply_failure", ct);
            return SlackOAuthAuthorizationResult.RecoveryRequired;
        }
    }

    private async Task MarkSecretStoredAsync(string attemptId, string botUserId, string tokenRef, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var current = await db.SlackOAuthAttempts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == attemptId, ct);
        if (current is null)
            throw new InvalidOperationException("OAuth authorization attempt was not found.");
        SlackStateTransitions.RequireOAuthAttemptTransition(current.Status, SlackOAuthAttemptStatus.SecretStored);
        var changed = await db.SlackOAuthAttempts
            .Where(item => item.Id == attemptId
                && item.Status == current.Status
                && (item.Status == SlackOAuthAttemptStatus.Consumed
                    || item.Status == SlackOAuthAttemptStatus.RecoveryRequired))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.BotUserId, botUserId)
                .SetProperty(item => item.BotTokenRef, tokenRef)
                .SetProperty(item => item.Status, SlackOAuthAttemptStatus.SecretStored)
                .SetProperty(item => item.SecretStoredAt, now)
                .SetProperty(item => item.FailureClass, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (changed == 0)
        {
            var existing = await db.SlackOAuthAttempts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == attemptId, ct);
            if (existing?.Status != SlackOAuthAttemptStatus.SecretStored && existing?.Status != SlackOAuthAttemptStatus.Applied)
                throw new InvalidOperationException("OAuth authorization attempt was changed by another operation.");
        }
    }

    private async Task<SlackOAuthAuthorizationResult> ApplyAuthorizationAsync(
        string attemptId,
        string agentAppId,
        string workspaceTeamId,
        string appId,
        string botUserId,
        string tokenRef,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var agentApp = await db.ManagedSlackAgentApps.SingleOrDefaultAsync(item => item.Id == agentAppId, ct);
        var attempt = await db.SlackOAuthAttempts.SingleOrDefaultAsync(item => item.Id == attemptId, ct);
        if (agentApp is null || attempt is null
            || agentApp.WorkspaceTeamId != workspaceTeamId
            || agentApp.AppId != appId
            || attempt.AgentAppId != agentAppId)
            return SlackOAuthAuthorizationResult.RecoveryRequired;

        SlackStateTransitions.RequireOAuthAttemptTransition(attempt.Status, SlackOAuthAttemptStatus.Applied);
        SlackStateTransitions.RequireAuthorizationTransition(agentApp.Authorization, SlackAuthorizationState.Authorized);
        if (attempt.Status == SlackOAuthAttemptStatus.Applied)
            return SlackOAuthAuthorizationResult.AlreadyApplied(attempt.BotUserId);
        if (agentApp.Authorization == SlackAuthorizationState.Authorized)
        {
            if (agentApp.AuthorizationAttemptId != attemptId || agentApp.BotUserId != botUserId)
                return SlackOAuthAuthorizationResult.Rejected("authorization_conflict");
            attempt.Status = SlackOAuthAttemptStatus.Applied;
            attempt.AppliedAt ??= now;
            attempt.UpdatedAt = now;
            await EnsureBindingObligationAsync(db, agentApp, now, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return SlackOAuthAuthorizationResult.AlreadyApplied(botUserId);
        }

        agentApp.BotUserId = botUserId;
        agentApp.BotTokenRef = tokenRef;
        agentApp.Authorization = SlackAuthorizationState.Authorized;
        agentApp.AuthorizationAttemptId = attemptId;
        agentApp.AuthorizedAt = now;
        agentApp.AuthorizationExpiresAt = null;
        agentApp.UpdatedAt = now;
        attempt.BotUserId = botUserId;
        attempt.BotTokenRef = tokenRef;
        attempt.Status = SlackOAuthAttemptStatus.Applied;
        attempt.AppliedAt = now;
        attempt.UpdatedAt = now;
        await EnsureBindingObligationAsync(db, agentApp, now, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return SlackOAuthAuthorizationResult.Accepted(botUserId);
    }

    private static async Task EnsureBindingObligationAsync(
        MohistDbContext db,
        ManagedSlackAgentAppRow agentApp,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var existing = await db.SlackAgentAppBindingObligations
            .SingleOrDefaultAsync(item => item.AgentAppId == agentApp.Id, ct);
        if (existing is null)
        {
            db.SlackAgentAppBindingObligations.Add(new SlackAgentAppBindingObligationRow
            {
                Id = $"bind_obligation_{Guid.NewGuid():N}",
                AgentAppId = agentApp.Id,
                AgentConnectionId = agentApp.AgentConnectionId,
                Status = SlackAgentAppBindingObligationStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            });
            return;
        }

        if (existing.Status is SlackAgentAppBindingObligationStatus.ConnectionDeleted or SlackAgentAppBindingObligationStatus.Conflict)
        {
            SlackStateTransitions.RequireBindingTransition(existing.Status, SlackAgentAppBindingObligationStatus.Pending);
            SlackStateTransitions.RequireBindingTransition(agentApp.BindingState, SlackAgentAppBindingState.Pending);
            existing.Status = SlackAgentAppBindingObligationStatus.Pending;
            existing.FailureClass = null;
            existing.UpdatedAt = now;
            agentApp.BindingState = SlackAgentAppBindingState.Pending;
            agentApp.BindingErrorClass = null;
            agentApp.UpdatedAt = now;
        }
    }

    private async Task MarkAttemptRecoveryRequiredAsync(string attemptId, string failureClass, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await db.SlackOAuthAttempts
                .Where(item => item.Id == attemptId
                    && (item.Status == SlackOAuthAttemptStatus.Consumed
                        || item.Status == SlackOAuthAttemptStatus.SecretStored
                        || item.Status == SlackOAuthAttemptStatus.RecoveryRequired))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, SlackOAuthAttemptStatus.RecoveryRequired)
                    .SetProperty(item => item.FailureClass, SlackSecretRedactor.Redact(failureClass))
                    .SetProperty(item => item.UpdatedAt, _timeProvider.GetUtcNow()), ct);
        }
        catch
        {
        }
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
