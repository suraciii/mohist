using System.Text.Json;

namespace Mohist.Server.SpecTests.Support;

internal sealed record ProductLoopProjectDto(string Id, string Name);
internal sealed record ProductLoopProjectVariablesDto(JsonElement? Vars, Dictionary<string, ProductLoopProjectStageVariablesDto?>? Stages);
internal sealed record ProductLoopProjectStageVariablesDto(JsonElement? Vars);
internal sealed record ProductLoopIssueDto(
        int Number,
        string Title,
        string Status,
        string Health,
        ProductLoopApprovalStateDto? ApprovalState,
        ProductLoopAttentionDto? Attention,
        string? WorkflowRunId,
        string? WorkflowStage,
        string? WorkflowStatus,
        string? Model,
        Dictionary<string, JsonElement>? AgentConfig,
        Dictionary<string, string>? StageModels);
internal sealed record ProductLoopApprovalStateDto(string Stage, string Status);
internal sealed record ProductLoopAttentionDto(string Reason);
internal sealed record ProductLoopIssueWorkflowStatusDto(ProductLoopWorkflowStatusDto? Workflow);
internal sealed record ProductLoopWorkflowStatusDto(string Status, string? CurrentStage, ProductLoopWorkflowStageDto[] Stages);
internal sealed record ProductLoopEventDto(long Id, string Type, string Time);
internal sealed record ProductLoopWorkflowYamlDto(string WorkflowRunId, string Yaml);
internal sealed record ProductLoopWorkflowStageDto(string Stage, string Status, ProductLoopWorkflowTaskDto[] Tasks, ProductLoopApprovalDto? ApprovalStatus);
internal sealed record ProductLoopWorkflowTaskDto(string Id, string Title, string? Uses, string Status);
internal sealed record ProductLoopApprovalDto(string? Result);
internal sealed record ProductLoopWorkDispatchDto(
        string WorkflowRunId,
        string WorkId,
        string? Uses,
        string? With,
        string? Variables,
        string WorkType,
        string? Stage,
        string? Title,
        string? ProjectId,
        int? IssueNumber);
