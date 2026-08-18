using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Api;

/// <summary>
/// Minimal validation HTTP API for the standalone Agent Jobs engine.
///
/// POST <c>/api/agent-jobs/validate</c> accepts a body of
/// <c>{ prompt, agentId, model, workspace }</c>, creates an <see cref="IAgentJobGrain"/>
/// with a generated key, dispatches it through the engine, awaits the job's
/// terminal result, and returns the job's status/message/output/artifacts.
///
/// IMPORTANT — validation-only surface: this endpoint is intentionally minimal
/// and is NOT the product API for agent execution. It exists solely to prove
/// the engine's dispatch and report path. There is no auth, no authority/permission model,
/// no polling, and no read-model. It must NOT be exposed on a production edge
/// as-is; treat it as a developer smoke-test endpoint.
///
/// The dispatch variables always populate <c>workspace.path</c> so the runner's
/// <c>WorkspaceManager.ensure</c> takes the existing early-return branch
/// (<c>workspace.ts:32-36</c>) — no runner-side code change is required
/// (Design Decision 8).
/// </summary>
public static class AgentJobController
{
    public const string ValidatePath = "/api/agent-jobs/validate";

    public static WebApplication MapAgentJobRoutes(this WebApplication app)
    {
        app.MapPost(ValidatePath, static (HttpRequest request, IGrainFactory grains, IOptions<AgentJobOptions> options,
            AgentQuerier agents, TimeProvider timeProvider, CancellationToken ct) =>
            HandleValidateAsync(request, grains, options, timeProvider, ct, agents));
        return app;
    }

    internal static async Task<IResult> HandleValidateAsync(
        HttpRequest request,
        IGrainFactory grains,
        IOptions<AgentJobOptions> options,
        TimeProvider timeProvider,
        CancellationToken ct,
        AgentQuerier? agents = null)
    {
        if (request.ContentLength is 0)
        {
            return ApiResults.BadRequest("Request body is required.", "validation_required");
        }

        AgentJobValidationRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<AgentJobValidationRequest>(
                request.Body,
                JSON.Options,
                ct);
        }
        catch (JsonException ex)
        {
            return ApiResults.BadRequest($"Invalid JSON: {ex.Message}", "validation_malformed");
        }

        if (body is null)
        {
            return ApiResults.BadRequest("Request body is required.", "validation_required");
        }

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(body.Prompt))
            errors.Add("prompt is required");
        if (string.IsNullOrWhiteSpace(body.AgentId))
            errors.Add("agentId is required");
        if (body.Workspace is not null
            && string.IsNullOrWhiteSpace(body.Workspace.Name)
            && string.IsNullOrWhiteSpace(body.Workspace.Path))
            errors.Add("workspace.name or workspace.path is required when workspace is provided");
        if (body.JobId is { Length: > 0 } && !IsValidJobId(body.JobId))
            errors.Add("jobId is invalid (max 128 chars, [A-Za-z0-9_.-]+ only)");

        if (errors.Count > 0)
        {
            return ApiResults.BadRequest(
                "Validation failed: " + string.Join("; ", errors),
                "validation_failed",
                new { fields = errors });
        }

        if (agents is not null)
        {
            var projectId = body.Workspace?.ProjectId;
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return ApiResults.BadRequest("workspace.projectId is required.", "validation_failed");
            }

            var agent = await agents.GetByIdAsync(projectId, body.AgentId!.Trim());
            if (agent is null)
            {
                return ApiResults.BadRequest("agentId must identify an Agent in workspace.projectId.", "validation_failed");
            }
        }

        var agentOptions = options.Value;
        var timeout = ResolveTimeout(agentOptions);
        var jobKey = ResolveJobKey(body);
        var grain = grains.GetGrain<IAgentJobGrain>(jobKey);

        var input = new AgentJobInput(
            Prompt: body.Prompt!.Trim(),
            Model: string.IsNullOrWhiteSpace(body.Model) ? null : body.Model.Trim(),
            WorkspaceName: body.Workspace?.Name,
            WorkspacePath: body.Workspace?.Path,
            ProjectId: body.Workspace?.ProjectId,
            AgentId: body.AgentId!.Trim());

        var waiter = grain.WaitForTerminalAsync();
        try
        {
            await grain.SubmitAsync(input);
        }
        catch (ArgumentException ex)
        {
            return ApiResults.BadRequest(ex.Message, "validation_failed");
        }

        AgentJobTerminalResult terminal;
        try
        {
            terminal = await waiter.WaitAsync(timeout, timeProvider, ct);
        }
        catch (OperationCanceledException)
            when (!ct.IsCancellationRequested)
        {
            return BuildTimeoutResponse(jobKey, timeout);
        }
        catch (TimeoutException)
        {
            return BuildTimeoutResponse(jobKey, timeout);
        }

        return Results.Ok(new ApiResponse<AgentJobValidationResponse>(
            true,
            new AgentJobValidationResponse(
                Status: ToStatusString(terminal.Status),
                Message: terminal.Message,
                Output: terminal.Output,
                Artifacts: terminal.ArtifactUploadIds ?? [],
                JobId: jobKey,
                FailureReason: terminal.FailureReason,
                ExitCode: terminal.ExitCode)));
    }

    private static TimeSpan ResolveTimeout(AgentJobOptions options)
    {
        var fromOptions = options.JobTimeout;
        if (fromOptions > TimeSpan.Zero)
            return fromOptions + TimeSpan.FromSeconds(30);

        return TimeSpan.FromMinutes(11);
    }

    private static string ResolveJobKey(AgentJobValidationRequest body)
    {
        var provided = body.JobId;
        if (string.IsNullOrWhiteSpace(provided))
            return $"agent-job-validate-{Guid.NewGuid():N}";

        var trimmed = provided.Trim();
        return trimmed.StartsWith("agent-job-", StringComparison.Ordinal)
            ? trimmed
            : $"agent-job-validate-{trimmed}";
    }

    private const int MaxJobIdLength = 128;

    private static bool IsValidJobId(string jobId)
    {
        if (jobId.Length > MaxJobIdLength)
            return false;
        for (var i = 0; i < jobId.Length; i++)
        {
            var c = jobId[i];
            var isAllowed =
                (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') ||
                c == '_' || c == '.' || c == '-';
            if (!isAllowed)
                return false;
        }
        return true;
    }

    private static IResult BuildTimeoutResponse(string jobKey, TimeSpan timeout)
    {
        var body = new AgentJobValidationResponse(
            Status: "failed",
            Message: $"Agent job '{jobKey}' did not complete within {timeout.TotalSeconds:N0}s.",
            Output: null,
            Artifacts: [],
            JobId: jobKey,
            FailureReason: "timeout",
            ExitCode: null);

        return Results.Ok(new ApiResponse<AgentJobValidationResponse>(true, body));
    }

    private static string ToStatusString(AgentJobStatus status) => status switch
    {
        AgentJobStatus.Completed => "completed",
        AgentJobStatus.Failed => "failed",
        AgentJobStatus.Cancelled => "cancelled",
        AgentJobStatus.Running => "running",
        AgentJobStatus.Pending => "pending",
        AgentJobStatus.Unknown => "unknown",
        AgentJobStatus.RecoverablyInterrupted => "recoverably-interrupted",
        AgentJobStatus.Interrupted => "interrupted",
        _ => "unknown",
    };
}

public sealed record AgentJobValidationRequest
{
    public string? Prompt { get; init; }
    public string? AgentId { get; init; }
    public string? Model { get; init; }
    public string? Uses { get; init; }
    public string? JobId { get; init; }
    public AgentJobValidationWorkspace? Workspace { get; init; }
}

public sealed record AgentJobValidationWorkspace
{
    public string? Name { get; init; }
    public string? Path { get; init; }
    public string? ProjectId { get; init; }
}

public sealed record AgentJobValidationResponse(
    string Status,
    string? Message,
    string? Output,
    IReadOnlyList<string> Artifacts,
    string JobId,
    string? FailureReason,
    int? ExitCode);
