using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Domain.Errors;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Api;

public static class IssueRoutes
{
    public static WebApplication MapIssueRoutes(this WebApplication app)
    {
        var issues = app.MapGroup("/api/issues/{issueId}");

        issues.MapPut("/", async (string issueId, UpdateIssueRequest req, IGrainFactory grains) =>
        {
            var grain = grains.GetGrain<IIssueGrain>(issueId);
            await grain.UpdateAsync(req.Title, req.Body);
            return ApiResults.Ok();
        });

        issues.MapPost("/archive", async (string issueId, IGrainFactory grains) =>
        {
            var grain = grains.GetGrain<IIssueGrain>(issueId);
            await grain.ArchiveAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/close", async (string issueId, IGrainFactory grains) =>
        {
            var grain = grains.GetGrain<IIssueGrain>(issueId);
            await grain.CloseAsync();
            return ApiResults.Ok();
        });

        var wf = issues.MapGroup("/workflow");

        wf.MapGet("/status", async (string issueId, IGrainFactory grains) =>
        {
            var grain = grains.GetGrain<IIssueGrain>(issueId);
            var status = await grain.GetWorkflowStatusAsync();
            return status is not null ? ApiResults.Ok(status) : ApiResults.NotFound("Workflow not found");
        });

        wf.MapPost("/start", async (string issueId, IGrainFactory grains) =>
        {
            var grain = grains.GetGrain<IIssueGrain>(issueId);
            await grain.StartWorkflowAsync();
            return ApiResults.Ok();
        });

        wf.MapPost("/stop", async (string issueId, IGrainFactory grains) =>
        {
            var wrId = await grains.GetGrain<IIssueGrain>(issueId).GetWorkflowRunIdAsync();
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).PauseAsync("user-requested");
            return ApiResults.Ok();
        });

        wf.MapPost("/resume", async (string issueId, IGrainFactory grains) =>
        {
            var wrId = await grains.GetGrain<IIssueGrain>(issueId).GetWorkflowRunIdAsync();
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).ResumeAsync();
            return ApiResults.Ok();
        });

        wf.MapPost("/approve", async (string issueId, IGrainFactory grains) =>
        {
            var wrId = await grains.GetGrain<IIssueGrain>(issueId).GetWorkflowRunIdAsync();
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).ApproveAsync();
            return ApiResults.Ok();
        });

        wf.MapPost("/reject", async (string issueId, RejectRequest? req, IGrainFactory grains) =>
        {
            var wrId = await grains.GetGrain<IIssueGrain>(issueId).GetWorkflowRunIdAsync();
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RejectAsync(req?.Reason);
            return ApiResults.Ok();
        });

        wf.MapPost("/retry", async (string issueId, IGrainFactory grains) =>
        {
            var wrId = await grains.GetGrain<IIssueGrain>(issueId).GetWorkflowRunIdAsync();
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RetryAsync();
            return ApiResults.Ok();
        });

        wf.MapPost("/rerun", async (string issueId, IGrainFactory grains) =>
        {
            var wrId = await grains.GetGrain<IIssueGrain>(issueId).GetWorkflowRunIdAsync();
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RerunAsync();
            return ApiResults.Ok();
        });

        wf.MapPost("/rebase", async (string issueId, IGrainFactory grains) =>
        {
            var wrId = await grains.GetGrain<IIssueGrain>(issueId).GetWorkflowRunIdAsync();
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).ResumeAsync();
            return ApiResults.Ok();
        });

        return app;
    }
}

public record UpdateIssueRequest(string Title, string? Body);
public record RejectRequest(string? Reason);
