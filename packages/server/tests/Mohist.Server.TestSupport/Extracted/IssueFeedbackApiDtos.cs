namespace Mohist.Server.TestSupport;

internal sealed record FeedbackApiProjectDto(string Id, string Name);

internal sealed record FeedbackApiFeedbackEnvelopeDto(bool Success, FeedbackApiFeedbackDto? Data, string? Error = null);
internal sealed record FeedbackApiFeedbackDto(
        string Id,
        int IssueNumber,
        string WorkflowRunId,
        string Stage,
        string Status,
        string Body,
        string CreatedAt,
        FeedbackApiFeedbackResolutionDto? Resolution = null);
internal sealed record FeedbackApiFeedbackResolutionDto(
        string? ResolutionTaskId,
        string? ResolvedAt,
        string? ResolutionSummary);

internal sealed record FeedbackApiIssueDetailDto(
        int Number,
        string Title,
        string Status,
        FeedbackApiFeedbackDto[] Feedback);

internal sealed record FeedbackApiIssueWorkflowStatusDto(FeedbackApiWorkflowStatusDto? Workflow);
internal sealed record FeedbackApiWorkflowStatusDto(string Status, string? CurrentStage, FeedbackApiWorkflowStageDto[] Stages);
internal sealed record FeedbackApiWorkflowStageDto(
        string Stage,
        string Status,
        FeedbackApiStageFeedbackDto[]? Feedback = null);
internal sealed record FeedbackApiStageFeedbackDto(
        string Id,
        string Body,
        string Status,
        string CreatedAt,
        FeedbackApiStageFeedbackResolutionDto? Resolution = null);
internal sealed record FeedbackApiStageFeedbackResolutionDto(
        string? ResolutionTaskId,
        string? ResolvedAt,
        string? ResolutionSummary);
