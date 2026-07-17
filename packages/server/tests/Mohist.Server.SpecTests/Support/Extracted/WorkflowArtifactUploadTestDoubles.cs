using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Storage;
using Xunit;
namespace Mohist.Server.SpecTests.Support;

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;
}

internal sealed class StubWorkContextResolver : IWorkflowArtifactUploadWorkContextResolver
{
    private readonly Dictionary<(string WorkflowRunId, string WorkId), WorkflowActiveWorkView> _views = new();

    public void Register(string workflowRunId, string workId, string taskRunId,
        string? stage = "build", string workType = "task", string? title = null,
        string? projectId = null, int? issueNumber = null)
    {
        _views[(workflowRunId, workId)] = new WorkflowActiveWorkView(
            WorkId: workId,
            WorkType: workType,
            Stage: stage ?? "build",
            TaskRunId: taskRunId,
            Title: title,
            ProjectId: projectId,
            IssueNumber: issueNumber);
    }

    public Task<WorkflowActiveWorkView?> ResolveAsync(string workflowRunId, string workId, CancellationToken cancellationToken = default)
    {
        _views.TryGetValue((workflowRunId, workId), out var view);
        return Task.FromResult(view);
    }
}
