using Mohist.Server.Workspace.Domain;

namespace Mohist.Server.Workspace.Grains;

public interface IWorkspaceGrain : IGrainWithStringKey
{
    Task<WorkspaceState?> GetAsync();
    Task<WorkspaceState> CreateManualAsync(string name, string[] repositoryNames, DateTimeOffset now);
    Task<WorkspaceState> CreateAsync(string name, WorkspaceOrigin origin, IReadOnlyList<string> repositoryNames, DateTimeOffset now);
    Task<WorkspaceState> EnsureIssueWorkspaceAsync(int issueNumber, string repositoryName, DateTimeOffset now);
    Task<WorkspaceState?> AddRepositoryAsync(string repoName);
    Task<WorkspaceState?> RemoveRepositoryAsync(string repoName);
    Task<WorkspaceState?> CloseAsync(DateTimeOffset now);
    Task ArchiveByIssueAsync(int issueNumber, DateTimeOffset now);
    Task ArchiveByOriginAsync(WorkspaceOrigin origin, DateTimeOffset now);
    Task<WorkspaceHome?> GetHomeAsync();
    Task<WorkspaceHome?> EnsureMaterializedOnAsync(string runnerId, string path, DateTimeOffset now);
    Task ClearHomeIfAsync(string runnerId);
}
