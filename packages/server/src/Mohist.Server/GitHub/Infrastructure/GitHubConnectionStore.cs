using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        string? pat = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.Owner = connection.Owner.Trim().ToLowerInvariant();
        connection.Repo = connection.Repo.Trim().ToLowerInvariant();
        connection.Approvers = NormalizeApprovers(connection.Approvers);
        connection.Validate(requireInstallationId: false);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == connection.ProjectId, ct)
            ?? throw new GitHubConnectionValidationException("project not found", "project_not_found");
        connection.RepositoryName = ResolveRepository(project.RepositoriesJson, connection.Owner, connection.Repo);
        if (connection.IdentityKind != GitHubIdentityKind.Pat)
            throw new GitHubConnectionValidationException(
                "GitHub App identity is reserved for a future connection flow; provide a PAT",
                "github_app_identity_not_supported");
        if (string.IsNullOrWhiteSpace(pat))
            throw new GitHubConnectionValidationException(
                "pat is required for an active GitHub connection",
                "pat_required");

        var duplicate = await db.GitHubConnections.AnyAsync(
            r => r.Owner == connection.Owner && r.Repo == connection.Repo, ct);
        if (duplicate)
            throw new GitHubConnectionConflictException(
                $"GitHub repository '{connection.Owner}/{connection.Repo}' is already connected to a project", "github_repository_already_connected");

        var now = _timeProvider.GetUtcNow();
        connection.Status = GitHubConnectionStatus.Active;
        connection.CreatedAt = now;
        connection.UpdatedAt = now;
        var apiSecretAddress = ApiSecretAddress(connection.ProjectId, connection.Id);
        await _secretStore.StoreAsync(
            apiSecretAddress,
            Encoding.UTF8.GetBytes(pat),
            ct);
        db.GitHubConnections.Add(ToRow(connection));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsOwnerRepoConflict(ex))
        {
            await _secretStore.DeleteAsync(apiSecretAddress, CancellationToken.None);
            throw new GitHubConnectionConflictException(
                $"GitHub repository '{connection.Owner}/{connection.Repo}' is already connected to a project", "github_repository_already_connected");
        }
        catch
        {
            await _secretStore.DeleteAsync(apiSecretAddress, CancellationToken.None);
            throw;
        }

        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await _secretStore.StoreAsync(WebhookSecretAddress(connection.ProjectId, connection.Id), Encoding.UTF8.GetBytes(secret), ct);
        return secret;
    }

    public async Task<GitHubConnection?> SetStatusAsync(string projectId, string id, string status, CancellationToken ct = default)
    {
        if (status is not (GitHubConnectionStatus.Active or GitHubConnectionStatus.Disabled))
            throw new GitHubConnectionValidationException("status must be one of active, disabled", "invalid_status");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubConnections.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Id == id, ct);
        if (row is null) return null;
        if (row.Status == status) return ToDomain(row);
        if (status == GitHubConnectionStatus.Active)
        {
            var pat = await _secretStore.LoadAsync(ApiSecretAddress(row.ProjectId, row.Id), ct);
            if (pat is null || string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(pat)))
                throw new GitHubConnectionValidationException(
                    "pat is required for an active GitHub connection",
                    "pat_required");
        }
        row.Status = status;
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
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

    /// <summary>
    /// Flips the NeedsAttention flag, set by the write-back writer when an
    /// operation hit 401/403 (the connection's GitHub identity needs
    /// operator attention). Clearing is an operator action and stays
    /// outside this minimal loop.
    /// </summary>
    public async Task<GitHubConnection?> MarkNeedsAttentionAsync(
        string projectId,
        string id,
        bool needsAttention,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubConnections.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Id == id, ct);
        if (row is null) return null;
        if (row.NeedsAttention == needsAttention) return ToDomain(row);
        row.NeedsAttention = needsAttention;
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    public async Task<byte[]?> LoadWebhookSecretAsync(string projectId, string id, CancellationToken ct = default) =>
        await _secretStore.LoadAsync(WebhookSecretAddress(projectId, id), ct);

    public static SecretStoreAddress WebhookSecretAddress(string projectId, string connectionId) =>
        new(projectId, $"{connectionId}:webhook", SecretKind.WebhookSecret);

    /// <summary>
    /// Fine-grained GitHub PAT (Issues read/write only). Stored per
    /// connection and consumed by the current comment port.
    /// </summary>
    public static SecretStoreAddress ApiSecretAddress(string projectId, string connectionId) =>
        new(new SecretOwnerAddress.GitHubConnection(projectId, connectionId), SecretKind.AppToken);

    private static string ResolveRepository(string repositoriesJson, string owner, string repo)
    {
        var repositories = JSON.Deserialize<List<RepositoryInfo>>(repositoriesJson) ?? [];
        var target = GitRemoteUrlNormalizer.Fingerprint($"https://github.com/{owner}/{repo}");
        if (target is null)
            throw new GitHubConnectionValidationException(
                $"Could not normalize 'https://github.com/{owner}/{repo}'", "repository_not_registered");
        var match = repositories
            .Select(r => (Repository: r, Fingerprint: GitRemoteUrlNormalizer.Fingerprint(r.GitUrl)))
            .Where(pair => pair.Fingerprint is not null)
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
        IdentityKind = row.IdentityKind,
        InstallationId = row.InstallationId,
        NeedsAttention = row.NeedsAttention,
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
        IdentityKind = connection.IdentityKind,
        InstallationId = connection.InstallationId,
        NeedsAttention = connection.NeedsAttention,
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
