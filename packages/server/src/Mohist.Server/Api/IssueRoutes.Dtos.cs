using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Api;

public record CreateIssueRequest(
    string Title,
    string? Body = null,
    string[]? Labels = null,
    string? Priority = null,
    string? Model = null,
    Dictionary<string, object?>? AgentConfig = null,
    Dictionary<string, string>? StageModels = null,
    string? WorkflowProfileId = null,
    string? RepositoryName = null,
    string? Risk = null,
    bool? IsDraft = null,
    string[]? AttachmentIds = null);

public record UpdateIssueRequest(
    string? Title = null,
    string? Body = null,
    string[]? Labels = null,
    string? Priority = null,
    string? Model = null,
    Dictionary<string, object?>? AgentConfig = null,
    Dictionary<string, string>? StageModels = null,
    Dictionary<string, Dictionary<string, string>>? StageVariables = null,
    bool? IsDraft = null,
    string[]? AttachmentIds = null);

public record CreateFeedbackRequest(string Stage, string Body);

public sealed record RebaseRequest(string? BaseBranch = null, RuntimeTaskRequest? ConflictResolver = null);

public sealed record RuntimeTaskRequest(
    string? Id = null,
    string? Title = null,
    string? Uses = null,
    Dictionary<string, object?>? With = null);

public record AddPrerequisiteRequest(int PrerequisiteNumber);

public record AddCommentRequest(string Body, string[]? AttachmentIds = null);

public sealed record AttachmentUploadResponse(
    string Id,
    string FileName,
    string? ContentType,
    long Size,
    string? ExpiresAt);

public record IssueTemplateRequest(string? ProjectTemplateId = null, string? Yaml = null, string? Template = null);

public sealed record IssueWorkflowProfileResponse(
    int IssueNumber,
    string ProjectId,
    string IssueId,
    string? SourceTemplateId,
    bool HasCustomTemplate,
    string? Yaml,
    string? WorkflowRunId,
    string ProfileId,
    string UpdateMode,
    VariableBundle Variables,
    string UpdatedAt,
    string TemplateSource);

public sealed record IssuePromptUpsertRequest(string? Body);
