using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Webhooks;
using Mohist.Server.Infrastructure.Events;
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

    public async Task<WebhookSubscription> CreateAsync(
        WebhookSubscription subscription, WebhookAuthInput? auth, byte[]? legacySigningSecret, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        NormalizeEventSelection(subscription);
        await ValidateAsync(subscription, existingId: null, ct);
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
        await StoreAuthAsync(subscription, auth, ct);
        if (legacySigningSecret is { Length: > 0 })
            await _secretStore.StoreAsync(SigningSecretAddress(subscription.ProjectId, subscription.Id), legacySigningSecret, ct);
        return subscription;
    }

    public async Task<WebhookSubscription?> UpdateAsync(
        string projectId,
        string id,
        WebhookSubscriptionPatch patch,
        CancellationToken ct = default)
    {
        var existing = await GetAsync(projectId, id, ct);
        if (existing is null) return null;
        var target = patch.ApplyTo(existing);
        NormalizeEventSelection(target);
        await ValidateAsync(target, existingId: id, ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WebhookSubscriptions.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Id == id, ct);
        if (row is null) return null;
        row.Name = target.Name.Trim();
        row.Match = target.Match;
        row.TargetUrl = target.TargetUrl;
        row.EventSelectionMode = target.EventSelectionMode;
        row.EventTypes = SerializeEventTypes(target.EventTypes);
        row.AuthType = target.AuthType;
        row.UpdatedAt = _timeProvider.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsNameConflict(ex))
        {
            throw new WebhookSubscriptionNameConflictException(projectId, row.Name);
        }
        if (patch.AuthProvided)
            await StoreAuthAsync(ToDomain(row), patch.Auth, ct);
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
        await _secretStore.StoreAsync(SigningSecretAddress(projectId, id), secret, ct);
        return true;
    }

    public async Task<bool> HasSigningSecretAsync(string projectId, string id, CancellationToken ct = default)
    {
        var secret = await _secretStore.LoadAsync(SigningSecretAddress(projectId, id), ct);
        return secret is { Length: > 0 };
    }

    /// <summary>Loads the legacy HMAC signing secret, if any. Preserved for existing subscriptions; not part of the v1 create flow.</summary>
    public async Task<byte[]?> LoadSigningSecretAsync(string projectId, string id, CancellationToken ct = default) =>
        await _secretStore.LoadAsync(SigningSecretAddress(projectId, id), ct);

    /// <summary>Resolves the endpoint-auth material for sending. Returns null when auth type is none or no credential is stored.</summary>
    public async Task<WebhookAuthMaterial> ResolveAuthMaterialAsync(WebhookSubscription subscription, CancellationToken ct = default)
    {
        if (subscription.AuthType == WebhookAuthType.None)
            return new WebhookAuthMaterial { AuthType = WebhookAuthType.None };

        var credential = await _secretStore.LoadAsync(AuthSecretAddress(subscription.ProjectId, subscription.Id), ct);
        if (credential is null || credential.Length == 0)
            return new WebhookAuthMaterial { AuthType = WebhookAuthType.None };

        return new WebhookAuthMaterial { AuthType = subscription.AuthType, Headers = DecodeHeaders(subscription.AuthType, credential) };
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

    private async Task ValidateAsync(WebhookSubscription subscription, string? existingId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subscription.Name))
            throw new WebhookSubscriptionValidationException("name is required", "name_required");
        if (string.IsNullOrWhiteSpace(subscription.TargetUrl))
            throw new WebhookSubscriptionValidationException("targetUrl is required", "target_url_required");
        if (!Uri.TryCreate(subscription.TargetUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new WebhookSubscriptionValidationException("targetUrl must be an absolute http or https URL", "invalid_target_url");

        if (subscription.EventSelectionMode == WebhookEventSelectionMode.Selected)
        {
            if (subscription.EventTypes.Count == 0)
                throw new WebhookSubscriptionValidationException("eventTypes must not be empty when eventSelectionMode is 'selected'", "event_types_empty");
            var catalog = new HashSet<string>(EventCatalog.All, StringComparer.Ordinal);
            var unknown = subscription.EventTypes.FirstOrDefault(t => !catalog.Contains(t));
            if (unknown is not null)
                throw new WebhookSubscriptionValidationException($"eventTypes contains unknown event type '{unknown}'", "event_type_unknown");
        }

        // CEL Match is an optional advanced filter — empty is valid.
        if (!string.IsNullOrWhiteSpace(subscription.Match))
        {
            var compiled = EventMatchExpression.Compile(subscription.Match);
            if (!compiled.IsSuccess)
                throw new WebhookSubscriptionMatchException(compiled.Diagnostic!);
        }

        if (subscription.AuthType is not (WebhookAuthType.None or WebhookAuthType.Bearer or WebhookAuthType.Basic or WebhookAuthType.Custom))
            throw new WebhookSubscriptionValidationException("authType must be one of none, bearer, basic, custom", "invalid_auth_type");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var duplicate = await db.WebhookSubscriptions.AnyAsync(s => s.ProjectId == subscription.ProjectId && s.Name == subscription.Name.Trim() && s.Id != existingId, ct);
        if (duplicate)
            throw new WebhookSubscriptionNameConflictException(subscription.ProjectId, subscription.Name.Trim());
    }

    private static void NormalizeEventSelection(WebhookSubscription subscription)
    {
        if (subscription.EventSelectionMode == WebhookEventSelectionMode.All)
            subscription.EventTypes = [];
        subscription.EventTypes = subscription.EventTypes
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
    }

    private async Task StoreAuthAsync(WebhookSubscription subscription, WebhookAuthInput? auth, CancellationToken ct)
    {
        var address = AuthSecretAddress(subscription.ProjectId, subscription.Id);
        if (auth is null || auth.Type == WebhookAuthType.None || auth.IsEmpty())
        {
            await _secretStore.DeleteAsync(address, ct);
            return;
        }
        await _secretStore.StoreAsync(address, auth.Encode(), ct);
    }

    private static IReadOnlyDictionary<string, string> DecodeHeaders(string authType, byte[] credential)
    {
        var text = Encoding.UTF8.GetString(credential);
        if (authType == WebhookAuthType.Bearer)
            return new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = "Bearer " + text };
        if (authType == WebhookAuthType.Basic)
            return new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) };
        if (authType == WebhookAuthType.Custom)
        {
            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(text, JSON.Options) ?? new();
            return new Dictionary<string, string>(headers, StringComparer.Ordinal);
        }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static SecretStoreAddress SigningSecretAddress(string projectId, string subscriptionId) =>
        new(projectId, subscriptionId, SecretKind.WebhookSecret);

    private static SecretStoreAddress AuthSecretAddress(string projectId, string subscriptionId) =>
        // ConnectionId is namespaced so v1 auth credentials coexist with any legacy signing secret
        // stored under the bare subscription id, without a new SecretKind or schema change.
        new(projectId, AuthConnectionId(subscriptionId), SecretKind.WebhookSecret);

    private static string AuthConnectionId(string subscriptionId) => subscriptionId + ":auth";

    private static string SerializeEventTypes(IReadOnlyList<string> types) =>
        JsonSerializer.Serialize(types.OrderBy(t => t, StringComparer.Ordinal).Distinct(StringComparer.Ordinal));

    private static IReadOnlyList<string> DeserializeEventTypes(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JSON.Options) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static WebhookSubscription ToDomain(WebhookSubscriptionRow row) => new()
    {
        Id = row.Id, ProjectId = row.ProjectId, Name = row.Name, Match = row.Match,
        TargetUrl = row.TargetUrl, Status = row.Status,
        EventSelectionMode = string.IsNullOrWhiteSpace(row.EventSelectionMode) ? WebhookEventSelectionMode.All : row.EventSelectionMode,
        EventTypes = DeserializeEventTypes(row.EventTypes),
        AuthType = string.IsNullOrWhiteSpace(row.AuthType) ? WebhookAuthType.None : row.AuthType,
        CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt,
    };

    private static WebhookSubscriptionRow ToRow(WebhookSubscription subscription) => new()
    {
        Id = subscription.Id, ProjectId = subscription.ProjectId, Name = subscription.Name, Match = subscription.Match,
        TargetUrl = subscription.TargetUrl, Status = subscription.Status,
        EventSelectionMode = subscription.EventSelectionMode, EventTypes = SerializeEventTypes(subscription.EventTypes),
        AuthType = subscription.AuthType, CreatedAt = subscription.CreatedAt, UpdatedAt = subscription.UpdatedAt,
    };

    private static WebhookDeliveryFailure ToFailureDomain(WebhookDeliveryFailureRow row) => new()
    {
        Id = row.Id, ProjectId = row.ProjectId, SubscriptionId = row.SubscriptionId, EventId = row.EventId,
        EventType = row.EventType, TargetUrl = row.TargetUrl, ResponseStatus = row.ResponseStatus,
        DurationMs = row.DurationMs, ErrorSummary = row.ErrorSummary, OccurredAt = row.OccurredAt,
    };

    private static WebhookDeliveryFailureRow ToRow(WebhookDeliveryFailure failure) => new()
    {
        Id = failure.Id, ProjectId = failure.ProjectId, SubscriptionId = failure.SubscriptionId, EventId = failure.EventId,
        EventType = failure.EventType, TargetUrl = failure.TargetUrl, ResponseStatus = failure.ResponseStatus,
        DurationMs = failure.DurationMs, ErrorSummary = failure.ErrorSummary, OccurredAt = failure.OccurredAt,
    };

    private static bool IsNameConflict(DbUpdateException ex) =>
        ex.InnerException is SqliteException sqlite
        && sqlite.SqliteErrorCode == 19
        && sqlite.Message.Contains("WebhookSubscriptions", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Endpoint-auth input from API/CLI. Credentials are encoded for secret storage, never returned by read APIs.</summary>
public sealed record WebhookAuthInput(
    string Type,
    string? Token,
    (string User, string Password)? Basic,
    IReadOnlyDictionary<string, string>? Headers)
{
    public bool IsEmpty() => Type switch
    {
        WebhookAuthType.Bearer => string.IsNullOrWhiteSpace(Token),
        WebhookAuthType.Basic => Basic is null,
        WebhookAuthType.Custom => Headers is null || Headers.Count == 0,
        _ => true,
    };

    public byte[] Encode() => Type switch
    {
        WebhookAuthType.Bearer => Encoding.UTF8.GetBytes(Token ?? string.Empty),
        WebhookAuthType.Basic => Encoding.UTF8.GetBytes($"{Basic?.User}:{Basic?.Password}"),
        WebhookAuthType.Custom => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Headers ?? new Dictionary<string, string>(), JSON.Options)),
        _ => Array.Empty<byte>(),
    };
}

/// <summary>Patch applied to an existing subscription. Only provided fields are changed.</summary>
public sealed class WebhookSubscriptionPatch
{
    public string? Name { get; init; }
    public string? Match { get; init; }
    public string? TargetUrl { get; init; }
    public string? EventSelectionMode { get; init; }
    public IReadOnlyList<string>? EventTypes { get; init; }
    public string? AuthType { get; init; }
    public WebhookAuthInput? Auth { get; init; }
    public bool AuthProvided { get; init; }

    public WebhookSubscription ApplyTo(WebhookSubscription existing) => new()
    {
        Id = existing.Id,
        ProjectId = existing.ProjectId,
        Name = Name ?? existing.Name,
        Match = Match ?? existing.Match,
        TargetUrl = TargetUrl ?? existing.TargetUrl,
        Status = existing.Status,
        EventSelectionMode = EventSelectionMode ?? existing.EventSelectionMode,
        EventTypes = EventTypes ?? existing.EventTypes,
        AuthType = AuthType ?? existing.AuthType,
        CreatedAt = existing.CreatedAt,
        UpdatedAt = existing.UpdatedAt,
    };
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
