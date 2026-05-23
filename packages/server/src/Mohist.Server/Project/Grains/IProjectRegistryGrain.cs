using ProjectInfo = Mohist.Server.Project.Domain.ProjectInfo;

namespace Mohist.Server.Project.Grains;

public interface IProjectRegistryGrain : IGrainWithStringKey
{
    Task<ProjectInfo?> GetByNameAsync(string name);
    Task<List<ProjectInfo>> GetAllAsync();
    Task<ProjectInfo> CreateAsync(string name, string path, string? baseBranch);
    Task<ProjectInfo?> UpdateAsync(string name, string? baseBranch);
    Task<bool> DeleteAsync(string name);
    Task<ProjectInfo?> GetCurrentAsync();
    Task<ProjectInfo?> SetCurrentAsync(string name);
}
