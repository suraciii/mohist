namespace Mohist.Server.Infrastructure.Data.Workspace;

public class WorkspaceRow
{
    public string ProjectId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string OriginKind { get; set; } = null!;
    public string OriginPayloadJson { get; set; } = "{}";
    public string RepositoriesJson { get; set; } = "[]";
    public string Status { get; set; } = "active";
    public string? HomeRunnerId { get; set; }
    public string? HomePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}
