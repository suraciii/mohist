namespace Mohist.Server.Infrastructure.Workspace;

public interface IWorkspaceService
{
    Task<WorkspaceStatus> GetStatusAsync(string projectPath, string projectName, int issueNumber, string baseBranch);
    Task<bool> ExistsAsync(string projectPath, string projectName, int issueNumber);
    Task RemoveAsync(string projectPath, string projectName, int issueNumber);
}

public class WorkspaceStatus
{
    public bool Exists { get; set; }
    public string? Branch { get; set; }
    public string? BaseBranch { get; set; }
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public bool RebaseInProgress { get; set; }
    public string[] ConflictingFiles { get; set; } = [];
}
