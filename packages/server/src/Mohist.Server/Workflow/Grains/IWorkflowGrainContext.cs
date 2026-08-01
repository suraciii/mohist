using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Orleans;

namespace Mohist.Server.Workflow.Grains;

internal interface IWorkflowGrainContext
{
    WorkflowRun? RunOrNull { get; }
    string GrainKey { get; }
    string? GetWorkflowProfileId();
    WorkflowDefinitionResolver DefinitionResolver { get; }
    WorkflowVariableResolver VariableResolver { get; }
    IGrainFactory Grains { get; }
    ILogger Log { get; }
    DateTimeOffset Now();
    IDispatchSnapshotStore DispatchSnapshotStore { get; }

    void CacheAssignedWorkerId(string? workerId);
    Task SaveAsync();
    Task SaveAsyncWithEvents(IReadOnlyList<WorkflowEvent> events);
    Task ReleaseCurrentStageLocks(string reason);

    string GetProjectId();
    int? GetIssueNumber();
}
