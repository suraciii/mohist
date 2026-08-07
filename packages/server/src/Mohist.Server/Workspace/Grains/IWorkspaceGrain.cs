using Mohist.Server.Workspace.Domain;

namespace Mohist.Server.Workspace.Grains;

public interface IWorkspaceGrain : IGrainWithStringKey
{
    Task<WorkspaceState?> GetAsync();
    Task<WorkspaceState> CreateManualAsync(string name, string[] repositoryNames, DateTimeOffset now);
    Task<WorkspaceState?> AddRepositoryAsync(string repoName);
    Task<WorkspaceState?> RemoveRepositoryAsync(string repoName);
    Task<WorkspaceState?> CloseAsync(DateTimeOffset now);
    Task<WorkspaceHome?> GetHomeAsync();
    Task<WorkspaceHome?> EnsureMaterializedOnAsync(string runnerId, string path, DateTimeOffset now);
    Task ClearHomeIfAsync(string runnerId);
}
