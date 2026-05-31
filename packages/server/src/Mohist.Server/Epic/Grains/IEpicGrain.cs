using Mohist.Server.Epics;

namespace Mohist.Server.Epic.Grains;

public interface IEpicGrain : IGrainWithStringKey
{
    Task<EpicDto> CreateAsync(string projectId, string title, string? description, string? priority);
    Task LinkIssueAsync(string issueId, int issueNumber, string projectId);
    Task UnlinkIssueAsync(string issueId, string projectId);
    Task<EpicDto> SetStatusAsync(string status);
}
