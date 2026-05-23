using Mohist.Server.Issue.Domain;

namespace Mohist.Server.Issue.Grains;

public interface IIssueCatalogGrain : IGrainWithStringKey
{
    Task<IssueInfo> CreateAsync(string title, string? body, string[]? labels, string? priority);
    Task<List<IssueInfo>> ListAsync();
}
