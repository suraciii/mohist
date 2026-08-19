using System.Text.Json.Serialization;

namespace Mohist.Server.Infrastructure.Workspace;

public class WorkspaceStatus
{
    [JsonRequired]
    public bool Exists { get; set; }
    public string? Reason { get; set; }
    public string? Branch { get; set; }
    public string? BaseBranch { get; set; }
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public bool RebaseInProgress { get; set; }
    public string[] ConflictingFiles { get; set; } = [];
}
