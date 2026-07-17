using Microsoft.Extensions.Logging;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Orleans;

namespace Mohist.Server.Workflow.Grains;

internal interface IWorkflowGrainContext
{
    WorkflowRun? RunOrNull { get; }
    string GrainKey { get; }
    WorkflowProfileManager ProfileManager { get; }
    IGrainFactory Grains { get; }
    ILogger Log { get; }
    DateTimeOffset Now();

    void CacheAssignedWorkerId(string? workerId);
    Task SaveAsync();
    Task SaveAsyncWithEvents(IReadOnlyList<WorkflowEvent> events);
    Task DispatchEvent(WorkflowEvent e);
    Task ReleaseCurrentStageLocks(string reason);

    string GetProjectId();
    int? GetIssueNumber();
}
