using Mohist.Server.Epic.Services;

namespace Mohist.Server.Epic.Grains;

public interface IEpicGrain : IGrainWithStringKey
{
    Task<EpicDto> CreateAsync(string projectId, string title, string? description, string? priority);
    Task LinkIssueAsync(string issueId, int issueNumber, string projectId);
    Task UnlinkIssueAsync(string issueId, string projectId);
    Task<IReadOnlyList<BatchMembershipOutcome>> LinkIssuesAsync(
        IReadOnlyList<BatchMembershipRequestItem> issues,
        string projectId);
    Task<IReadOnlyList<BatchMembershipOutcome>> UnlinkIssuesAsync(
        IReadOnlyList<BatchMembershipRequestItem> issues,
        string projectId);
    Task<EpicDto> SetStatusAsync(string status);
    Task<EpicDto> PauseAsync(string? reason);
    Task<EpicDto> ResumeAsync();
    Task<EpicDto> StartAsync();
    Task<EpicDto> ReopenAsync();
    Task<EpicDto?> UpdateAsync(string? title, string? description, string? priority);
    Task<EpicDto?> AutoMarkDoneIfReadyAsync();
    Task<EpicDto?> RecomputeProgressAsync();
}
