using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Api;

public record RunnerReportRequest(
    string WorkId,
    string Status,
    string? WorkflowRunId = null,
    string? TaskRunId = null,
    string? ProjectId = null,
    string? Message = null,
    System.Text.Json.JsonElement? Output = null,
    int? ExitCode = null,
    string[]? ArtifactUploadIds = null,
    string? OwnerKind = null,
    string? AgentJobId = null,
    List<RuntimeTaskInput>? AddTasks = null,
    ExecutionError? Error = null,
    string? AgentSessionId = null,
    string? AgentTurnId = null,
    string? Runtime = null,
    string? RuntimeSessionId = null);
