using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

public static class WorkflowEventRoutes
{
    public static WebApplication MapWorkflowEventRoutes(this WebApplication app)
    {
        var byProject = app.MapGroup("/api/projects/{projectRef}/issues/{number:int}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        byProject.MapGet("/events", async (HttpContext context, int number, int? limit, IssueQuerier issues, WorkflowEventQuerier eventQuery) =>
        {
            var project = context.GetResolvedProject();

            var issue = await issues.GetInfoAsync(project.Id, number);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");

            var ct = HttpContextRequestAborted(context);
            var merged = await eventQuery.ListIssueEventsAsync(project.Id, number, issue.WorkflowRunId, limit ?? 200, ct);
            var response = merged
                .Select(StoredCloudEventDto.From)
                .ToList();

            return ApiResults.Ok(response);
        });

        app.MapGet("/api/workflow-runs/{workflowRunId}/events", async (string workflowRunId, int? limit, WorkflowQuerier workflowReader, WorkflowEventQuerier eventQuery) =>
        {
            if (await WorkflowRoutes.EnsureWorkflowRunExistsAsync(workflowRunId, workflowReader) is { } failure)
                return failure;

            var list = await eventQuery.ListWorkflowEventsAsync(workflowRunId, limit ?? 200);
            return ApiResults.Ok(list.Select(StoredCloudEventDto.From).ToList());
        });

        return app;
    }

    private static CancellationToken HttpContextRequestAborted(HttpContext context) =>
        context.RequestAborted;

}

public sealed record StoredCloudEventDto(
    long Id,
    string EventId,
    string Source,
    string Type,
    string SpecVersion,
    string? Subject,
    string Time,
    string? DataContentType,
    System.Text.Json.JsonElement Data,
    Dictionary<string, string> Extensions)
{
    public static StoredCloudEventDto From(StoredCloudEvent stored) =>
        new(
            stored.Id,
            stored.Envelope.Id,
            stored.Envelope.Source.ToString(),
            stored.Envelope.Type,
            stored.Envelope.SpecVersion,
            stored.Envelope.Subject,
            stored.Envelope.Time.ToString("o"),
            stored.Envelope.DataContentType,
            stored.Envelope.Data ?? default,
            new Dictionary<string, string>(stored.Envelope.Extensions));
}
