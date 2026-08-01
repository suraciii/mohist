using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Webhooks;
using Mohist.Server.Infrastructure.Events.Matching;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Webhooks.Domain;

namespace Mohist.Server.Webhooks.Services;

public sealed class WebhookSubscriptionStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ISecretStore _secretStore;
    private readonly TimeProvider _timeProvider;

    public WebhookSubscriptionStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        ISecretStore secretStore,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _secretStore = secretStore;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<WebhookSubscription>> ListAsync(string projectId, bool includeArchived = true, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.WebhookSubscriptions.AsNoTracking().Where(s => s.ProjectId == projectId);
        if (!includeArchived)
            query = query.Where(s => s.Status != WebhookSubscriptionStatus.Archived);
        var rows = await query.OrderBy(s => s.Name).ThenBy(s => s.Id).ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    public async Task<WebhookSubscription?> GetAsync(string projectId, string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WebhookSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Id == id, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<WebhookSubscription> CreateAsync(WebhookSubscription subscription, byte[]? secret, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        await ValidateAsync(subscription.ProjectId, subscription.Name, subscription.Match, subscription.TargetUrl, null, ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        subscription.Status = WebhookSubscriptionStatus.Active;
        subscription.CreatedAt = now;
        subscription.UpdatedAt = now;
        db.WebhookSubscriptions.Add(ToRow(subscription));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsNameConflict(ex))
        {
            throw new WebhookSubscriptionNameConflictException(subscription.ProjectId, subscription.Name);
        }
        if (secret is { Length: > 0 })
            await _secretStore.StoreAsync(SecretAddress(subscription.ProjectId, subscription.Id), secret, ct);
        return subscription;
    }

    public async Task<WebhookSubscription?> UpdateAsync(
        string projectId,
        string id,
        string? name,
        string? match,
        string? targetUrl,
        IReadOnlySet<string> fields,
        CancellationToken ct = default)
    {
        var existing = await GetAsync(projectId, id, ct);
        if (existing is null) return null;
        var newName = fields.Contains(nameof(WebhookSubscription.Name)) ? name : existing.Name;
        var newMatch = fields.Contains(nameof(WebhookSubscription.Match)) ? match : existing.Match;
        var newTargetUrl = fields.Contains(nameof(WebhookSubscription.TargetUrl)) ? targetUrl : existing.TargetUrl;
        await ValidateAsync(projectId, newName, newMatch, newTargetUrl, id, ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WebhookSubscriptions.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Id == id, ct);
        if (row is null) return null;
        row.Name = newName!.Trim();
        row.Match = newMatch!;
        row.TargetUrl = newTargetUrl!;
        row.UpdatedAt = _timeProvider.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsNameConflict(ex))
        {
            throw new WebhookSubscriptionNameConflictException(projectId, row.Name);
        }
        return ToDomain(row);
    }

    public async Task<WebhookSubscription?> SetStatusAsync(string projectId, string id, string status, CancellationToken ct = default)
    {
        if (status is not (WebhookSubscriptionStatus.Active or WebhookSubscriptionStatus.Disabled or WebhookSubscriptionStatus.Archived))
            throw new WebhookSubscriptionValidationException("status must be one of active, disabled, archived", "invalid_status");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WebhookSubscriptions.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Id == id, ct);
        if (row is null) return null;
        if (row.Status == status) return ToDomain(row);
        row.Status = status;
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    public async Task<bool> RotateSecretAsync(string projectId, string id, byte[] secret, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length == 0)
            throw new WebhookSubscriptionValidationException("secret cannot be empty", "secret_empty");
        var existing = await GetAsync(projectId, id, ct);
        if (existing is null) return false;
        await _secretStore.StoreAsync(SecretAddress(projectId, id), secret, ct);
        return true;
    }

    public async Task<bool> HasSecretAsync(string projectId, string id, CancellationToken ct = default)
    {
        var secret = await _secretStore.LoadAsync(SecretAddress(projectId, id), ct);
        return secret is { Length: > 0 };
    }

    public async Task RecordFailureAsync(WebhookDeliveryFailure failure, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.WebhookDeliveryFailures.Add(ToRow(failure));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WebhookDeliveryFailure>> ListFailuresAsync(string projectId, string? subscriptionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.WebhookDeliveryFailures.AsNoTracking().Where(f => f.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(subscriptionId))
            query = query.Where(f => f.SubscriptionId == subscriptionId);
        var rows = await query.ToListAsync(ct);
        return rows.Select(ToFailureDomain)
            .OrderByDescending(f => f.OccurredAt)
            .ToList();
    }

    private async Task ValidateAsync(string projectId, string? name, string? match, string? targetUrl, string? existingId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new WebhookSubscriptionValidationException("name is required", "name_required");
        if (string.IsNullOrWhiteSpace(match))
            throw new WebhookSubscriptionValidationException("match is required", "match_required");
        var compiled = EventMatchExpression.Compile(match);
        if (!compiled.IsSuccess)
            throw new WebhookSubscriptionMatchException(compiled.Diagnostic!);
        ValidateTargetUrl(targetUrl);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var duplicate = await db.WebhookSubscriptions.AnyAsync(s => s.ProjectId == projectId && s.Name == name.Trim() && s.Id != existingId, ct);
        if (duplicate)
            throw new WebhookSubscriptionNameConflictException(projectId, name.Trim());
    }

    private static void ValidateTargetUrl(string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
            throw new WebhookSubscriptionValidationException("targetUrl is required", "target_url_required");
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new WebhookSubscriptionValidationException("targetUrl must be an absolute http or https URL", "invalid_target_url");
    }

    private static SecretStoreAddress SecretAddress(string projectId, string subscriptionId) =>
        new(projectId, subscriptionId, SecretKind.WebhookSecret);

    private static WebhookSubscription ToDomain(WebhookSubscriptionRow row) => new()
    {
        Id = row.Id, ProjectId = row.ProjectId, Name = row.Name, Match = row.Match,
        TargetUrl = row.TargetUrl, Status = row.Status, CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt,
    };

    private static WebhookSubscriptionRow ToRow(WebhookSubscription subscription) => new()
    {
        Id = subscription.Id, ProjectId = subscription.ProjectId, Name = subscription.Name, Match = subscription.Match,
        TargetUrl = subscription.TargetUrl, Status = subscription.Status, CreatedAt = subscription.CreatedAt, UpdatedAt = subscription.UpdatedAt,
    };

    private static WebhookDeliveryFailure ToFailureDomain(WebhookDeliveryFailureRow row) => new()
    {
        Id = row.Id, ProjectId = row.ProjectId, SubscriptionId = row.SubscriptionId, EventId = row.EventId,
        EventType = row.EventType, TargetUrl = row.TargetUrl, ErrorSummary = row.ErrorSummary, OccurredAt = row.OccurredAt,
    };

    private static WebhookDeliveryFailureRow ToRow(WebhookDeliveryFailure failure) => new()
    {
        Id = failure.Id, ProjectId = failure.ProjectId, SubscriptionId = failure.SubscriptionId, EventId = failure.EventId,
        EventType = failure.EventType, TargetUrl = failure.TargetUrl, ErrorSummary = failure.ErrorSummary, OccurredAt = failure.OccurredAt,
    };

    private static bool IsNameConflict(DbUpdateException ex) =>
        ex.InnerException is SqliteException sqlite
        && sqlite.SqliteErrorCode == 19
        && sqlite.Message.Contains("WebhookSubscriptions", StringComparison.OrdinalIgnoreCase);
}

public sealed class WebhookSubscriptionValidationException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class WebhookSubscriptionMatchException(MatchDiagnostic diagnostic) : Exception(diagnostic.Message)
{
    public MatchDiagnostic Diagnostic { get; } = diagnostic;
}

public sealed class WebhookSubscriptionNameConflictException(string projectId, string name)
    : Exception($"A webhook subscription named '{name}' already exists in project '{projectId}'.")
{
    public string ProjectId { get; } = projectId;
    public string Name { get; } = name;
}
