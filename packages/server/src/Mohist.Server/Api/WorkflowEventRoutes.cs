using CloudNative.CloudEvents;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Api;

public static class WorkflowEventRoutes
{
    public static WebApplication MapWorkflowEventRoutes(this WebApplication app)
    {
        var byProject = app.MapGroup("/api/projects/{projectRef}/issues/{number:int}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        byProject.MapGet("/events", async (HttpContext context, int number, int? limit, IssueQuerier issues, IEventStore events) =>
        {
            var project = context.GetResolvedProject();

            var issue = await issues.GetInfoAsync(project.Id, number);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");

            var ct = HttpContextRequestAborted(context);
            var take = limit ?? 200;
            var issueEvents = await events.ListIssueEventsAsync(issue.Id, take, ct);

            IReadOnlyList<StoredCloudEvent> workflowEvents = issue.WorkflowRunId is { } wrId
                ? await events.ListAsync(wrId, take, ct)
                : [];

            // Merge by per-source sequence id; issue events and workflow
            // events live in separate tables with their own id sequences,
            // so we sort by Time for chronological order.
            var merged = issueEvents
                .Concat(workflowEvents)
                .OrderBy(e => e.Envelope.Time)
                .Select(StoredCloudEventDto.From)
                .ToList();

            return ApiResults.Ok(merged);
        });

        app.MapGet("/api/workflow-runs/{workflowRunId}/events", async (string workflowRunId, int? limit, IEventStore events) =>
        {
            var list = await events.ListAsync(workflowRunId, limit ?? 200);
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
