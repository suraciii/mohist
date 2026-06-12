using System.Text.Json;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Project.Grains;

public interface IProjectGrain : IGrainWithStringKey
{
    Task<ProjectInfo?> GetAsync();
    Task<ProjectInfo> CreateAsync(string name);
    Task<ProjectInfo?> UpdateAsync();
    Task DeleteAsync();
    Task<List<RepositoryInfo>> ListRepositoriesAsync();
    Task<ProjectInfo?> AddRepositoryAsync(string repoName, string gitUrl, string? baseBranch, bool? isDefault = null);
    Task<ProjectInfo?> UpdateRepositoryAsync(string repoName, string? newName = null, string? gitUrl = null, string? baseBranch = null, bool? isDefault = null);
    Task<ProjectInfo?> RemoveRepositoryAsync(string repoName);
    Task<ProjectInfo?> SetDefaultRepositoryAsync(string repoName);
    Task<ProjectVariablesBag?> GetVariablesAsync();
    Task<ProjectVariablesBag?> PatchVariableAsync(string name, JsonElement value);
    Task<ProjectVariablesBag?> DeleteVariableAsync(string name);
    Task<ProjectVariablesBag?> PatchStageVariableAsync(string stage, string name, JsonElement value);
    Task<ProjectVariablesBag?> DeleteStageVariableAsync(string stage, string name);
}
