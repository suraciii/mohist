using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Project.Grains;

public interface IProjectGrain : IGrainWithStringKey
{
    Task<ProjectInfo?> GetAsync();
    Task<ProjectInfo> CreateAsync(string name, RepositoryInfo initialRepository);
    Task<ProjectInfo?> UpdateAsync();
    Task DeleteAsync();
    Task<List<RepositoryInfo>> ListRepositoriesAsync();
    Task<ProjectInfo?> AddRepositoryAsync(string repoName, string gitUrl, string? baseBranch, bool? setDefault = null);
    Task<ProjectInfo?> UpdateRepositoryAsync(string repoName, string? gitUrl = null, string? baseBranch = null);
    Task<ProjectRepositoryUpdateOutcome> UpdateRepositoryWithReceiptAsync(string repoName, string? gitUrl, string? baseBranch, string commandId, long? expectedRevision);
    Task<ProjectInfo?> RemoveRepositoryAsync(string repoName);
    Task<ProjectRepositoryRemovalOutcome> RemoveRepositoryWithReceiptAsync(string repoName, string commandId, long? expectedRevision);
    Task<ProjectInfo?> SetDefaultRepositoryAsync(string repoName);
    Task<long> GetRepositoryBindingRevisionAsync();
    Task<ProjectVariablesBag?> GetVariablesAsync();
    Task<ProjectVariablesBag?> PatchVariableAsync(string name, JsonElement value);
    Task<ProjectVariablesBag?> DeleteVariableAsync(string name);
    Task<ProjectVariablesBag?> PatchStageVariableAsync(string stage, string name, JsonElement value);
    Task<ProjectVariablesBag?> DeleteStageVariableAsync(string stage, string name);

    /// <summary>
    /// Replace the Project default execution configuration. Validates via
    /// <see cref="AgentConfigSchema"/> plus the <c>provider/model</c> form;
    /// an invalid default throws <see cref="ArgumentException"/> and leaves
    /// any previous default untouched. A success replaces the prior value
    /// (one default per Project) and returns the updated Project.
    /// </summary>
    Task<ProjectInfo?> SetDefaultExecutionConfigAsync(ExecutionConfigHint config);
}

public enum ProjectRepositoryRemovalOutcome
{
    Removed,
    AlreadyApplied,
    ProjectNotFound,
}

public enum ProjectRepositoryUpdateOutcome
{
    Updated,
    AlreadyApplied,
    ProjectNotFound,
}
