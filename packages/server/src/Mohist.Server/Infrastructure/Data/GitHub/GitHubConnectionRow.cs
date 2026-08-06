using System.Text.Json;

namespace Mohist.Server.Infrastructure.Data.GitHub;

public sealed class GitHubConnectionRow
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Owner { get; set; }
    public required string Repo { get; set; }
    public required string RepositoryName { get; set; }
    public required string IntakeLabel { get; set; }
    public required string FeedMode { get; set; }
    public required string ApproversJson { get; set; }
    public required string Status { get; set; }
    public required string IdentityKind { get; set; }
    public string? InstallationId { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
