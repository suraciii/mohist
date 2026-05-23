using Mohist.Server.Issue.Domain;

namespace Mohist.Server.Issue.Grains;

public interface IIssueCatalogGrain : IGrainWithStringKey
{
    Task<IssueInfo> CreateAsync(
        string title,
        string? body,
        string[]? labels,
        string? priority,
        string? model = null,
        Dictionary<string, string>? stageModels = null);

    Task<List<IssueInfo>> ListAsync(
        string? stage = null,
        string? label = null,
        string? priority = null,
        bool? archived = null,
        bool? all = null);

    Task<IssueInfo?> GetByNumberAsync(int number);
    Task<IssueInfo?> RemoveAsync(int number);
}
