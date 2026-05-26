using ProjectInfo = Mohist.Server.Project.Queries.ProjectInfo;

namespace Mohist.Server.Project.Grains;

public interface IProjectGrain : IGrainWithStringKey
{
    Task<ProjectInfo?> GetByNameAsync(string name);
    Task<ProjectInfo?> GetByIdAsync(string id);
    Task<List<ProjectInfo>> GetAllAsync();
    Task<ProjectInfo> CreateAsync(string name, string path, string? baseBranch);
    Task<ProjectInfo?> UpdateAsync(string name, string? baseBranch);
    Task<bool> DeleteAsync(string name);
}

[GenerateSerializer]
public sealed record ProjectState(
    [property: Id(0)] Dictionary<string, ProjectInfo> Projects);
