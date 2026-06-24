using Microsoft.Extensions.Logging;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;

namespace Mohist.Server.Events.Subscriptions;

[Subscription(Type = "com.mohist.issue.work-completed")]
public sealed class EpicAutoDoneHandler : ICloudEventHandler<IssueWorkCompleted>
{
    private readonly EpicQuerier _epicQuerier;
    private readonly IGrainFactory _grains;
    private readonly ILogger<EpicAutoDoneHandler> _log;

    public EpicAutoDoneHandler(
        EpicQuerier epicQuerier,
        IGrainFactory grains,
        ILogger<EpicAutoDoneHandler> log)
    {
        _epicQuerier = epicQuerier;
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent<IssueWorkCompleted> evt) => true;

    public async Task HandleAsync(CloudEvent<IssueWorkCompleted> evt, CancellationToken ct)
    {
        if (!evt.Extensions.TryGetValue("projectid", out var projectId) || string.IsNullOrWhiteSpace(projectId))
        {
            _log.LogDebug("work-completed event missing projectid extension; skipping (event {EventId})", evt.Id);
            return;
        }
        if (!evt.Extensions.TryGetValue("issueid", out var issueId) || string.IsNullOrWhiteSpace(issueId))
        {
            _log.LogDebug("work-completed event missing issueid extension; skipping (event {EventId})", evt.Id);
            return;
        }

        var epicId = await _epicQuerier.GetEpicIdForIssueAsync(projectId, issueId).ConfigureAwait(false);
        if (epicId is null)
        {
            return;
        }

        try
        {
            var grain = _grains.GetGrain<IEpicGrain>($"{projectId}:{epicId}");
            await grain.AutoMarkDoneIfReadyAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Epic auto-done handler failed for project {ProjectId} epic {EpicId} issue {IssueId}; relying on reconciliation sweep",
                projectId, epicId, issueId);
        }
    }
}