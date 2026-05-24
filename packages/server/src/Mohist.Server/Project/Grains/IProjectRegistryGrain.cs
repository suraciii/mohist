using ProjectInfo = Mohist.Server.Project.Domain.ProjectInfo;

namespace Mohist.Server.Project.Grains;

public interface IProjectRegistryGrain : IGrainWithStringKey
{
    Task<ProjectInfo?> GetByNameAsync(string name);
    Task<ProjectInfo?> GetByIdAsync(string id);
    Task<List<ProjectInfo>> GetAllAsync();
    Task<ProjectInfo> CreateAsync(string name, string path, string? baseBranch);
    Task<ProjectInfo?> UpdateAsync(string name, string? baseBranch);
    Task<bool> DeleteAsync(string name);
    Task<ProjectInfo?> GetCurrentAsync();
    Task<ProjectInfo?> SetCurrentAsync(string name);
}

[GenerateSerializer]
public sealed record ProjectRegistryState(
    [property: Id(0)] Dictionary<string, ProjectInfo> Projects,
    [property: Id(1)] string? CurrentProjectName);
