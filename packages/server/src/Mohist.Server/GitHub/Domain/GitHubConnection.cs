namespace Mohist.Server.GitHub.Domain;

public sealed class GitHubConnection
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public IReadOnlyList<string> Approvers { get; set; } = [];
    public string Status { get; set; } = GitHubConnectionStatus.Active;
    public string? InstallationId { get; set; }
    public string? RepositoryNodeId { get; set; }
    public bool ReconnectRequired { get; set; }
    public bool NeedsAttention { get; set; }
    public bool NeedsReprojection { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorDetail { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public void Validate(bool requireInstallationId = true)
    {
        if (string.IsNullOrWhiteSpace(Owner))
            throw new GitHubConnectionValidationException("owner is required", "owner_required");
        if (string.IsNullOrWhiteSpace(Repo))
            throw new GitHubConnectionValidationException("repo is required", "repo_required");
        if (Status is not (GitHubConnectionStatus.Active or GitHubConnectionStatus.Disabled))
            throw new GitHubConnectionValidationException("status must be one of active, disabled", "invalid_status");
        if (requireInstallationId && string.IsNullOrWhiteSpace(InstallationId))
            throw new GitHubConnectionValidationException("installationId is required", "installation_id_required");
        if (requireInstallationId && string.IsNullOrWhiteSpace(RepositoryNodeId))
            throw new GitHubConnectionValidationException("repositoryNodeId is required", "repository_node_id_required");
        if (ReconnectRequired && Status != GitHubConnectionStatus.Disabled)
            throw new GitHubConnectionValidationException("reconnectRequired connections must be disabled", "invalid_reconnect_state");
    }
}

public sealed record GitHubConnectionStatusChange(GitHubConnection Connection, bool Changed);

public static class GitHubConnectionStatus
{
    public const string Active = "active";
    public const string Disabled = "disabled";
}

public sealed class GitHubConnectionValidationException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class GitHubConnectionConflictException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}
