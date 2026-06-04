using Mohist.Server.Workflow.Prompts.Domain;

namespace Mohist.Server.Workflow.Prompts;

public interface IProjectTemplateStore
{
    Task<IReadOnlyList<ProjectTemplate>> GetForProjectAsync(string projectId);

    Task<ProjectTemplate?> GetAsync(string projectId, string key);

    Task<ProjectTemplate> UpsertAsync(
        string projectId,
        string key,
        string body,
        string displayName,
        string description,
        IReadOnlyList<string> tags,
        string? stage);

    Task DeleteAsync(string projectId, string key);
}
