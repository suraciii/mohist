using System.Text.Json;

namespace Mohist.Server.Infrastructure.Data.GitHub;

public sealed class GitHubConnectionRow
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Owner { get; set; }
    public required string Repo { get; set; }
    public required string RepositoryName { get; set; }
    public required string ApproversJson { get; set; }
    public required string Status { get; set; }
    public string? InstallationId { get; set; }
    public string? RepositoryNodeId { get; set; }
    public bool ReconnectRequired { get; set; }
    public required bool NeedsAttention { get; set; }
    public bool NeedsReprojection { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorDetail { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
