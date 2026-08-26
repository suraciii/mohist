namespace Mohist.Server.Infrastructure.Data.Workflow;

public sealed record WorkflowArtifactBindingIntent(
    string WorkId,
    string TaskRunId,
    string[] UploadIds,
    DateTimeOffset RecordedAt,
    string? ProjectId,
    int? IssueNumber);
