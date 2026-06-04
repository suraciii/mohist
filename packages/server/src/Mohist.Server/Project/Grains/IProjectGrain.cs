using System.Text.Json;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Querying;

namespace Mohist.Server.Project.Grains;

public interface IProjectGrain : IGrainWithStringKey
{
    Task<ProjectInfo?> GetAsync();
    Task<ProjectInfo> CreateAsync(string name, string path, string? baseBranch);
    Task<ProjectInfo?> UpdateAsync(string? baseBranch);
    Task DeleteAsync();
    Task<List<RepositoryInfo>> ListRepositoriesAsync();
    Task<ProjectInfo?> AddRepositoryAsync(string repoName, string? path, string? remote, string? baseBranch);
    Task<ProjectInfo?> RemoveRepositoryAsync(string repoName);
    Task<ProjectInfo?> SetDefaultRepositoryAsync(string repoName);
    Task<ProjectVariablesBag?> GetVariablesAsync();
    Task<ProjectVariablesBag?> PatchVariableAsync(string name, JsonElement value);
    Task<ProjectVariablesBag?> DeleteVariableAsync(string name);
    Task<ProjectVariablesBag?> PatchStageVariableAsync(string stage, string name, JsonElement value);
    Task<ProjectVariablesBag?> DeleteStageVariableAsync(string stage, string name);
}
