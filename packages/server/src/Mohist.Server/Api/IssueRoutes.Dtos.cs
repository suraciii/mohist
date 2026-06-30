using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Api;

public record CreateIssueRequest(
    string Title,
    string? Body = null,
    Dictionary<string, string>? Labels = null,
    string? Priority = null,
    string? Model = null,
    string? ModelVariant = null,
    Dictionary<string, object?>? AgentConfig = null,
    Dictionary<string, string>? StageModels = null,
    Dictionary<string, string>? StageModelVariants = null,
    string? WorkflowProfileId = null,
    string? RepositoryName = null,
    string? Risk = null,
    bool? IsDraft = null,
    string[]? AttachmentIds = null);

/// <summary>
/// PATCH body for issue updates. Includes a <see cref="Raw"/> JsonElement
/// so the route handler can detect explicit <c>null</c> fields
/// (System.Text.Json cannot distinguish "absent" from "null" on plain
/// nullable reference types). The model-metadata fields
/// (<c>model</c>, <c>modelVariant</c>, <c>stageModels</c>,
/// <c>stageModelVariants</c>) follow the spec's "present-but-null = clear"
/// semantic; <see cref="Raw"/> lets the handler detect presence.
///
/// <see cref="Fields"/> carries a set of simple-field names (Title, Body,
/// Labels, Priority, IsDraft, AttachmentIds) that the request body actually
/// mentioned. The grain uses it to honor three-state semantics on those
/// fields (absent = leave alone, present-and-null = clear, present-with-value
/// = replace). <see cref="Raw"/> carries the same presence signal for
/// model-metadata fields and is consumed by the route handler via
/// <c>BuildUpdatePatch</c> on the issue-routes helper layer.
/// </summary>
public record UpdateIssueRequest
{
    private static readonly JsonSerializerOptions JsonOptions = JSON.Options;

    public string? Title { get; init; }
    public string? Body { get; init; }
    public Dictionary<string, string>? Labels { get; init; }
    public string? Priority { get; init; }
    public string? Model { get; init; }
    public string? ModelVariant { get; init; }
    public Dictionary<string, object?>? AgentConfig { get; init; }
    public Dictionary<string, string>? StageModels { get; init; }
    public Dictionary<string, string>? StageModelVariants { get; init; }
    public Dictionary<string, Dictionary<string, string>>? StageVariables { get; init; }
    public bool? IsDraft { get; init; }
    public string[]? AttachmentIds { get; init; }
    public string? WorkflowProfileId { get; init; }

    /// <summary>
    /// Raw JSON body, captured at bind time. Used by the route handler to
    /// inspect model-metadata field presence without re-parsing the request
    /// body. A field present in the raw body but null on the bound record
    /// means "explicit clear"; a field absent from the raw body means "no
    /// change".
    /// </summary>
    public JsonElement Raw { get; init; }

    /// <summary>
    /// Set of simple-field names that the request body actually mentioned.
    /// The grain's <c>UpdateFullAsync</c> uses it to honor three-state
    /// semantics on Title, Body, Labels, Priority, IsDraft, and
    /// AttachmentIds: absent = leave alone, present-and-null = clear,
    /// present-with-value = replace. Model-metadata fields are not tracked
    /// here — the route handler reads presence off <see cref="Raw"/>.
    /// </summary>
    public IReadOnlySet<string> Fields { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public bool Contains(string fieldName) => Fields.Contains(fieldName);

    public static async ValueTask<UpdateIssueRequest?> BindAsync(HttpContext context)
    {
        JsonElement raw;
        try
        {
            raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (raw.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (raw.TryGetProperty("title", out _)) fields.Add(nameof(Title));
        if (raw.TryGetProperty("body", out _)) fields.Add(nameof(Body));
        if (raw.TryGetProperty("labels", out _)) fields.Add(nameof(Labels));
        if (raw.TryGetProperty("priority", out _)) fields.Add(nameof(Priority));
        if (raw.TryGetProperty("isDraft", out _)) fields.Add(nameof(IsDraft));
        if (raw.TryGetProperty("attachmentIds", out _)) fields.Add(nameof(AttachmentIds));
        if (raw.TryGetProperty("workflowProfileId", out _)) fields.Add(nameof(WorkflowProfileId));

        return new UpdateIssueRequest
        {
            Title = GetString(raw, "title"),
            Body = GetString(raw, "body"),
            Labels = GetStringMap(raw, "labels"),
            Priority = GetString(raw, "priority"),
            Model = GetString(raw, "model"),
            ModelVariant = GetString(raw, "modelVariant"),
            AgentConfig = GetObject(raw, "agentConfig"),
            StageModels = GetStringMap(raw, "stageModels"),
            StageModelVariants = GetStringMap(raw, "stageModelVariants"),
            StageVariables = GetNestedStringMap(raw, "stageVariables"),
            IsDraft = GetNullableBool(raw, "isDraft"),
            AttachmentIds = GetStringArray(raw, "attachmentIds"),
            WorkflowProfileId = GetString(raw, "workflowProfileId"),
            Raw = raw,
            Fields = fields,
        };
    }

    private static string? GetString(JsonElement raw, string name) =>
        raw.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static Dictionary<string, string>? GetStringMap(JsonElement raw, string name)
    {
        if (!raw.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object)
            return null;
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String) continue;
            dict[prop.Name] = prop.Value.GetString()!;
        }
        return dict;
    }

    private static Dictionary<string, object?>? GetObject(JsonElement raw, string name) =>
        raw.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(el.GetRawText(), JsonOptions)
            : null;

    private static Dictionary<string, Dictionary<string, string>>? GetNestedStringMap(JsonElement raw, string name)
    {
        if (!raw.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object)
            return null;
        var dict = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var stage in el.EnumerateObject())
        {
            if (stage.Value.ValueKind != JsonValueKind.Object) continue;
            var inner = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in stage.Value.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                inner[prop.Name] = prop.Value.GetString()!;
            }
            dict[stage.Name] = inner;
        }
        return dict;
    }

    private static bool? GetNullableBool(JsonElement raw, string name) =>
        raw.TryGetProperty(name, out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            ? el.GetBoolean()
            : null;

    private static string[]? GetStringArray(JsonElement raw, string name)
    {
        if (!raw.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return null;
        return el.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray();
    }
}

public record CreateFeedbackRequest(string Stage, string Body);

public sealed record RebaseRequest(string? BaseBranch = null);

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

/// <summary>
/// Single bucket in the completion time-series. <c>Boundary</c> is the
/// ISO calendar boundary that the bucket represents (yyyy-MM-dd for
/// day buckets, the Monday of the ISO week for week buckets). Counts
/// are the number of issues that reached the terminal state within
/// the bucket, deduped per (issue, type).
/// </summary>
public sealed record CompletionMetricsBucketDto(
    string Boundary,
    int Completed,
    int Failed);

/// <summary>
/// Response shape for the completion metrics endpoint. <c>Bucket</c>
/// is one of <c>day</c> / <c>week</c>; <c>Window</c> is the trailing
/// time range the series covers. <c>Buckets</c> is dense: every
/// bucket in the window is present, even when its counts are zero.
/// </summary>
public sealed record CompletionMetricsResponse(
    string Bucket,
    CompletionMetricsWindowDto Window,
    CompletionMetricsBucketDto[] Buckets);

public sealed record CompletionMetricsWindowDto(
    string From,
    string To);

/// <summary>
/// Response shape for the approval-wait metrics endpoint. <c>Window</c>
/// is the trailing 7-day range the aggregation covers. <c>SampleCount</c>
/// distinguishes a true zero-sample window (null stats) from a completed
/// approval with a measurable or zero wait.
/// </summary>
public sealed record ApprovalWaitMetricsResponse(
    ApprovalWaitMetricsWindowDto Window,
    int SampleCount,
    double? AverageSeconds,
    double? MedianSeconds,
    double? MaxSeconds);

public sealed record ApprovalWaitMetricsWindowDto(
    string From,
    string To);

/// <summary>
/// Response shape for the AI quality metrics endpoint. Both trailing
/// windows are returned together so callers can compare recent and
/// longer-term quality in one read.
/// </summary>
public sealed record QualityMetricsResponse(
    QualityMetricsWindowDto Window7d,
    QualityMetricsWindowDto Window30d);

/// <summary>
/// One trailing window in the quality aggregation. <see cref="SampleCount"/>
/// is the number of shipped issues in the window; it distinguishes a true
/// zero-sample window (null rates) from a window where every issue was
/// first-time-right (rate 1 with SampleCount > 0).
/// </summary>
public sealed record QualityMetricsWindowDto(
    string From,
    string To,
    int SampleCount,
    double? FirstTimeRightRate,
    StageReworkRateDto[] Stages);

/// <summary>
/// Per-stage rework rate for a trailing window. <see cref="EnteredCount"/>
/// is the number of shipped-in-window issues that entered the stage; a
/// null rate means no issue entered the stage in that window.
/// </summary>
public sealed record StageReworkRateDto(
    string Stage,
    int EnteredCount,
    double? ReworkRate);
