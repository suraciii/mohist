using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mohist.Server.GitHub.Ports;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.GitHub;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Project.Domain;

namespace Mohist.Server.GitHub.Infrastructure;

public sealed class GitHubConnectionStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ISecretStore _secretStore;
    private readonly TimeProvider _timeProvider;

    public GitHubConnectionStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        ISecretStore secretStore,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _secretStore = secretStore;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<GitHubConnection>> ListAsync(string projectId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.GitHubConnections.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.Owner)
            .ThenBy(r => r.Repo)
            .ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    public async Task<GitHubConnection?> GetAsync(string projectId, string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubConnections.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Id == id, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<GitHubConnection?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubConnections.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        return row is null ? null : ToDomain(row);
    }

    /// <summary>
    /// Finds the connection serving a repository (one connection per
    /// <c>(owner, repo)</c>, enforced at create). The write-back writer
    /// resolves the connection from the link's repository name.
    /// </summary>
    public async Task<GitHubConnection?> GetByRepositoryAsync(
        string projectId,
        string repositoryName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubConnections.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.RepositoryName == repositoryName, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<string> CreateAsync(
        GitHubConnection connection,
        GitHubRepositoryInstallation installation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(installation);
        connection.Owner = installation.Owner.Trim().ToLowerInvariant();
        connection.Repo = installation.Repo.Trim().ToLowerInvariant();
        connection.InstallationId = installation.InstallationId;
        connection.RepositoryNodeId = installation.RepositoryNodeId;
        connection.ReconnectRequired = false;
        connection.Approvers = NormalizeApprovers(connection.Approvers);
        connection.Validate();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == connection.ProjectId, ct)
            ?? throw new GitHubConnectionValidationException("project not found", "project_not_found");
        connection.RepositoryName = ResolveRepository(project.RepositoriesJson, connection.Owner, connection.Repo, connection.RepositoryName);
        var existing = await db.GitHubConnections.FirstOrDefaultAsync(
            r => r.Owner == connection.Owner && r.Repo == connection.Repo, ct);
        if (existing is not null)
        {
            if (!existing.ReconnectRequired
                && !string.Equals(existing.RepositoryNodeId, connection.RepositoryNodeId, StringComparison.Ordinal))
                throw new GitHubConnectionConflictException(
                    $"GitHub repository '{connection.Owner}/{connection.Repo}' is already connected to a project", "github_repository_already_connected");
            if (!string.Equals(existing.ProjectId, connection.ProjectId, StringComparison.Ordinal)
                || !string.Equals(existing.RepositoryName, connection.RepositoryName, StringComparison.Ordinal))
                throw new GitHubConnectionConflictException(
                    $"GitHub repository '{connection.Owner}/{connection.Repo}' is already connected to a project", "github_repository_already_connected");

            var existingSecret = await _secretStore.LoadAsync(
                WebhookSecretAddress(existing.ProjectId, existing.Id), ct);
            if (existingSecret is null || existingSecret.Length == 0)
                throw new GitHubConnectionValidationException(
                    "The connection has no webhook secret; restore it from a database backup",
                    "github_webhook_secret_missing");

            existing.Owner = connection.Owner;
            existing.Repo = connection.Repo;
            existing.RepositoryName = connection.RepositoryName;
            existing.InstallationId = connection.InstallationId;
            existing.RepositoryNodeId = connection.RepositoryNodeId;
            existing.Status = GitHubConnectionStatus.Active;
            existing.ReconnectRequired = false;
            existing.NeedsAttention = false;
            existing.NeedsReprojection = true;
            existing.LastErrorCode = null;
            existing.LastErrorDetail = null;
            existing.LastErrorAt = null;
            existing.UpdatedAt = _timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct);
            connection.Id = existing.Id;
            connection.Status = existing.Status;
            connection.NeedsAttention = existing.NeedsAttention;
            connection.NeedsReprojection = existing.NeedsReprojection;
            connection.LastErrorCode = existing.LastErrorCode;
            connection.LastErrorDetail = existing.LastErrorDetail;
            connection.LastErrorAt = existing.LastErrorAt;
            connection.CreatedAt = existing.CreatedAt;
            connection.UpdatedAt = existing.UpdatedAt;
            return Encoding.UTF8.GetString(existingSecret);
        }

        var now = _timeProvider.GetUtcNow();
        connection.Status = GitHubConnectionStatus.Active;
        connection.NeedsReprojection = false;
        connection.CreatedAt = now;
        connection.UpdatedAt = now;
        var webhookSecretAddress = WebhookSecretAddress(connection.ProjectId, connection.Id);
        var webhookSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (_secretStore is AesGcmSecretStore aes)
            {
                await aes.StoreAtomicallyAsync(db, [new SecretStoreWrite(
                    webhookSecretAddress, Encoding.UTF8.GetBytes(webhookSecret))], ct);
            }
            else
            {
                await _secretStore.StoreAsync(webhookSecretAddress, Encoding.UTF8.GetBytes(webhookSecret), ct);
            }
            db.GitHubConnections.Add(ToRow(connection));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsOwnerRepoConflict(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new GitHubConnectionConflictException(
                $"GitHub repository '{connection.Owner}/{connection.Repo}' is already connected to a project", "github_repository_already_connected");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        return webhookSecret;
    }

    public async Task<GitHubConnection?> SetStatusAsync(string projectId, string id, string status, CancellationToken ct = default) =>
        (await SetStatusWithTransitionAsync(projectId, id, status, ct))?.Connection;

    public async Task<GitHubConnectionStatusChange?> SetStatusWithTransitionAsync(
        string projectId,
        string id,
        string status,
        CancellationToken ct = default)
    {
        if (status is not (GitHubConnectionStatus.Active or GitHubConnectionStatus.Disabled))
            throw new GitHubConnectionValidationException("status must be one of active, disabled", "invalid_status");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.GitHubConnections.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Id == id, ct);
        if (existing is null) return null;
        if (existing.Status == status)
            return new GitHubConnectionStatusChange(ToDomain(existing), false);
        if (status == GitHubConnectionStatus.Active)
        {
            if (existing.ReconnectRequired
                || string.IsNullOrWhiteSpace(existing.InstallationId)
                || string.IsNullOrWhiteSpace(existing.RepositoryNodeId))
            {
                throw new GitHubConnectionValidationException(
                    "Reconnect the GitHub App installation before enabling this connection",
                    "github_app_reconnect_required");
            }
        }

        var now = _timeProvider.GetUtcNow();
        var expected = status == GitHubConnectionStatus.Active
            ? GitHubConnectionStatus.Disabled
            : GitHubConnectionStatus.Active;
        var candidates = db.GitHubConnections
            .Where(row => row.ProjectId == projectId && row.Id == id && row.Status == expected);
        var changed = status == GitHubConnectionStatus.Active
            ? await candidates.ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, status)
                .SetProperty(row => row.NeedsReprojection, true)
                .SetProperty(row => row.UpdatedAt, now), ct)
            : await candidates.ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, status)
                .SetProperty(row => row.UpdatedAt, now), ct);
        var row = await db.GitHubConnections.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == id, ct);
        return row is null ? null : new GitHubConnectionStatusChange(ToDomain(row), changed == 1);
    }

    public async Task<IReadOnlyList<GitHubConnection>> ListPendingReprojectionsAsync(
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.GitHubConnections.AsNoTracking()
            .Where(row => row.Status == GitHubConnectionStatus.Active && row.NeedsReprojection)
            .ToListAsync(ct);
        return rows
            .OrderBy(row => row.UpdatedAt)
            .Select(ToDomain)
            .ToList();
    }

    public async Task<GitHubConnection?> ClearReprojectionPendingAsync(
        string projectId,
        string id,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        await db.GitHubConnections
            .Where(row => row.ProjectId == projectId
                && row.Id == id
                && row.Status == GitHubConnectionStatus.Active
                && row.NeedsReprojection)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.NeedsReprojection, false)
                .SetProperty(row => row.UpdatedAt, now), ct);
        var row = await db.GitHubConnections.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == id, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<GitHubConnection?> UpdateApproversAsync(string projectId, string id, IReadOnlyList<string>? approvers, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubConnections.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Id == id, ct);
        if (row is null) return null;
        // Absent field means no change; an explicit empty array clears the list.
        if (approvers is null) return ToDomain(row);
        row.ApproversJson = SerializeApprovers(NormalizeApprovers(approvers));
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    public async Task<GitHubConnection?> MarkNeedsAttentionAsync(
        string projectId,
        string id,
        bool needsAttention,
        CancellationToken ct = default) =>
        await SetAttentionAsync(projectId, id, needsAttention, null, null, reconnect: false, ct);

    public async Task<GitHubConnection?> MarkInstallationUnavailableAsync(
        string projectId,
        string id,
        string code,
        string detail,
        CancellationToken ct = default) =>
        await SetAttentionAsync(projectId, id, true, code, detail, reconnect: true, ct);

    private async Task<GitHubConnection?> SetAttentionAsync(
        string projectId,
        string id,
        bool needsAttention,
        string? code,
        string? detail,
        bool reconnect,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubConnections.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Id == id, ct);
        if (row is null) return null;
        row.NeedsAttention = needsAttention;
        if (reconnect)
        {
            row.Status = GitHubConnectionStatus.Disabled;
            row.ReconnectRequired = true;
        }
        row.LastErrorCode = code;
        row.LastErrorDetail = detail;
        row.LastErrorAt = code is null ? row.LastErrorAt : _timeProvider.GetUtcNow();
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    public async Task<byte[]?> LoadWebhookSecretAsync(string projectId, string id, CancellationToken ct = default) =>
        await _secretStore.LoadAsync(WebhookSecretAddress(projectId, id), ct);

    public static SecretStoreAddress WebhookSecretAddress(string projectId, string connectionId) =>
        new(projectId, $"{connectionId}:webhook", SecretKind.WebhookSecret);

    private static string ResolveRepository(string repositoriesJson, string owner, string repo, string? requestedName)
    {
        var repositories = JSON.Deserialize<List<RepositoryInfo>>(repositoriesJson) ?? [];
        var target = GitRemoteUrlNormalizer.Fingerprint($"https://github.com/{owner}/{repo}");
        if (target is null)
            throw new GitHubConnectionValidationException(
                $"Could not normalize 'https://github.com/{owner}/{repo}'", "repository_not_registered");
        var match = repositories
            .Select(r => (Repository: r, Fingerprint: GitRemoteUrlNormalizer.Fingerprint(r.GitUrl)))
            .Where(pair => pair.Fingerprint is not null)
            .Where(pair => string.IsNullOrWhiteSpace(requestedName)
                || string.Equals(pair.Repository.Name, requestedName, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(pair =>
                pair.Fingerprint!.Fingerprint == target.Fingerprint
                || string.Equals(pair.Fingerprint.Canonical, target.Canonical, StringComparison.OrdinalIgnoreCase));
        if (match.Repository is not null)
            return match.Repository.Name;
        throw new GitHubConnectionValidationException(
            $"No repository registered in this project matches 'https://github.com/{owner}/{repo}'; register the repository first", "repository_not_registered");
    }

    private static bool IsOwnerRepoConflict(DbUpdateException ex) =>
        ex.InnerException is SqliteException sqlite
        && sqlite.SqliteErrorCode == 19
        && sqlite.Message.Contains("GitHubConnections", StringComparison.OrdinalIgnoreCase);

    private static GitHubConnection ToDomain(GitHubConnectionRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        Owner = row.Owner,
        Repo = row.Repo,
        RepositoryName = row.RepositoryName,
        Approvers = DeserializeApprovers(row.ApproversJson),
        Status = row.Status,
        InstallationId = row.InstallationId,
        RepositoryNodeId = row.RepositoryNodeId,
        ReconnectRequired = row.ReconnectRequired,
        NeedsAttention = row.NeedsAttention,
        NeedsReprojection = row.NeedsReprojection,
        LastErrorCode = row.LastErrorCode,
        LastErrorDetail = row.LastErrorDetail,
        LastErrorAt = row.LastErrorAt,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };

    private static GitHubConnectionRow ToRow(GitHubConnection connection) => new()
    {
        Id = connection.Id,
        ProjectId = connection.ProjectId,
        Owner = connection.Owner,
        Repo = connection.Repo,
        RepositoryName = connection.RepositoryName,
        ApproversJson = SerializeApprovers(connection.Approvers),
        Status = connection.Status,
        InstallationId = connection.InstallationId,
        RepositoryNodeId = connection.RepositoryNodeId,
        ReconnectRequired = connection.ReconnectRequired,
        NeedsAttention = connection.NeedsAttention,
        NeedsReprojection = connection.NeedsReprojection,
        LastErrorCode = connection.LastErrorCode,
        LastErrorDetail = connection.LastErrorDetail,
        LastErrorAt = connection.LastErrorAt,
        CreatedAt = connection.CreatedAt,
        UpdatedAt = connection.UpdatedAt,
    };

    private static IReadOnlyList<string> NormalizeApprovers(IReadOnlyList<string>? approvers) =>
        (approvers ?? [])
        .Select(a => a.Trim())
        .Where(a => a.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string SerializeApprovers(IReadOnlyList<string> approvers) =>
        JsonSerializer.Serialize(approvers.OrderBy(a => a, StringComparer.Ordinal).Distinct(StringComparer.Ordinal), JSON.Options);

    private static IReadOnlyList<string> DeserializeApprovers(string json)
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
}
