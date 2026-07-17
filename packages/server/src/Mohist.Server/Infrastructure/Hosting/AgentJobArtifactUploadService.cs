using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Infrastructure.Hosting;

public sealed class AgentJobArtifactUploadService : IScopedService
{
    private readonly WorkflowArtifactUploadService _uploadService;

    public AgentJobArtifactUploadService(
        IDbContextFactory<MohistDbContext> dbFactory,
        IWorkflowArtifactStorage storage,
        IGrainFactory grains,
        ILogger<WorkflowArtifactUploadService> log)
    {
        _uploadService = new WorkflowArtifactUploadService(
            dbFactory,
            storage,
            new AgentJobArtifactUploadWorkContextResolver(grains),
            log);
    }

    public Task<WorkflowArtifactUploadResult> UploadAsync(
        WorkflowArtifactUploadRequest request,
        CancellationToken cancellationToken = default) =>
        _uploadService.UploadAsync(request, cancellationToken);

    private sealed class AgentJobArtifactUploadWorkContextResolver : IWorkflowArtifactUploadWorkContextResolver
    {
        private readonly IGrainFactory _grains;

        public AgentJobArtifactUploadWorkContextResolver(IGrainFactory grains)
        {
            _grains = grains;
        }

        public async Task<WorkflowActiveWorkView?> ResolveAsync(
            string workflowRunId,
            string workId,
            CancellationToken cancellationToken = default)
        {
            var job = _grains.GetGrain<IAgentJobGrain>(workflowRunId);
            var snapshot = await job.GetRuntimeSnapshotAsync();
            if (snapshot.Status != AgentJobStatus.Running
                || !string.Equals(snapshot.CurrentWorkId, workId, StringComparison.Ordinal))
                return null;

            return new WorkflowActiveWorkView(
                WorkId: workId,
                WorkType: "agent-job",
                Stage: "agent",
                TaskRunId: workId,
                Title: "Agent Job",
                ProjectId: null,
                IssueNumber: null);
        }
    }
}
