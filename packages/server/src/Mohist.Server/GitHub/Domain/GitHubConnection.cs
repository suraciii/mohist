namespace Mohist.Server.GitHub.Domain;

public sealed class GitHubConnection
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public string IntakeLabel { get; set; } = GitHubIntakeLabel.Default;
    public string FeedMode { get; set; } = GitHubFeedMode.Start;
    public IReadOnlyList<string> Approvers { get; set; } = [];
    public string Status { get; set; } = GitHubConnectionStatus.Active;
    public string IdentityKind { get; set; } = GitHubIdentityKind.App;
    public string? InstallationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public void Validate(bool requireInstallationId = true)
    {
        if (string.IsNullOrWhiteSpace(Owner))
            throw new GitHubConnectionValidationException("owner is required", "owner_required");
        if (string.IsNullOrWhiteSpace(Repo))
            throw new GitHubConnectionValidationException("repo is required", "repo_required");
        if (string.IsNullOrWhiteSpace(IntakeLabel))
            throw new GitHubConnectionValidationException("intakeLabel is required", "intake_label_required");
        if (IntakeLabel.StartsWith("mohist:", StringComparison.Ordinal))
            throw new GitHubConnectionValidationException("intakeLabel must not start with 'mohist:' (reserved for the write-back label family)", "intake_label_prefix_reserved");
        if (FeedMode is not (GitHubFeedMode.Start or GitHubFeedMode.Backlog))
            throw new GitHubConnectionValidationException("feedMode must be one of start, backlog", "invalid_feed_mode");
        if (Status is not (GitHubConnectionStatus.Active or GitHubConnectionStatus.Disabled))
            throw new GitHubConnectionValidationException("status must be one of active, disabled", "invalid_status");
        if (IdentityKind is not (GitHubIdentityKind.App or GitHubIdentityKind.Pat))
            throw new GitHubConnectionValidationException("identityKind must be one of app, pat", "invalid_identity_kind");
        if (requireInstallationId && IdentityKind == GitHubIdentityKind.App && string.IsNullOrWhiteSpace(InstallationId))
            throw new GitHubConnectionValidationException("installationId is required when identityKind is 'app'", "installation_id_required");
    }
}

public static class GitHubConnectionStatus
{
    public const string Active = "active";
    public const string Disabled = "disabled";
}

public static class GitHubFeedMode
{
    public const string Start = "start";
    public const string Backlog = "backlog";
}

public static class GitHubIdentityKind
{
    public const string App = "app";
    public const string Pat = "pat";
}

public static class GitHubIntakeLabel
{
    public const string Default = "mohist";
}

public sealed class GitHubConnectionValidationException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class GitHubConnectionConflictException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}
