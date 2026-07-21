using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Api;

public record CreateIssueRequest
{
    private static readonly JsonSerializerOptions JsonOptions = JSON.Options;

    public string Title { get; init; } = string.Empty;
    public string? Body { get; init; }
    public Dictionary<string, string>? Labels { get; init; }
    public string? Priority { get; init; }
    public string? Model { get; init; }
    public string? ModelVariant { get; init; }
    public Dictionary<string, string>? StageModels { get; init; }
    public Dictionary<string, string>? StageModelVariants { get; init; }
    public string? WorkflowProfileId { get; init; }
    public string? RepositoryName { get; init; }
    public string? Risk { get; init; }
    public bool? IsDraft { get; init; }
    public string[]? AttachmentIds { get; init; }
    public int[]? PrerequisiteNumbers { get; init; }
    public int? ParentIssueNumber { get; init; }

    /// <summary>
    /// Raw JSON body captured at bind time so the route handler can
    /// inspect fields outside the typed record (e.g. open-shape
    /// <c>agentConfig</c>) without re-parsing the request body. Required
    /// to enforce the <c>agentConfig</c> forbidden-key validation at the
    /// API boundary.
    /// </summary>
    public JsonElement Raw { get; init; }

    public static async ValueTask<CreateIssueRequest?> BindAsync(HttpContext context)
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

        return new CreateIssueRequest
        {
            Title = GetString(raw, "title") ?? string.Empty,
            Body = GetString(raw, "body"),
            Labels = GetStringMap(raw, "labels"),
            Priority = GetString(raw, "priority"),
            Model = GetString(raw, "model"),
            ModelVariant = GetString(raw, "modelVariant"),
            StageModels = GetStringMap(raw, "stageModels"),
            StageModelVariants = GetStringMap(raw, "stageModelVariants"),
            WorkflowProfileId = GetString(raw, "workflowProfileId"),
            RepositoryName = GetString(raw, "repositoryName"),
            Risk = GetString(raw, "risk"),
            IsDraft = GetNullableBool(raw, "isDraft"),
            AttachmentIds = GetStringArray(raw, "attachmentIds"),
            PrerequisiteNumbers = GetNullableIntArray(raw, "prerequisiteNumbers"),
            ParentIssueNumber = GetNullableInt(raw, "parentIssueNumber"),
            Raw = raw,
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

    private static int[]? GetNullableIntArray(JsonElement raw, string name)
    {
        if (!raw.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return null;
        return el.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()).ToArray();
    }

    private static int? GetNullableInt(JsonElement raw, string name) =>
        raw.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32()
            : null;
}

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
    public Dictionary<string, string>? StageModels { get; init; }
    public Dictionary<string, string>? StageModelVariants { get; init; }
    public Dictionary<string, Dictionary<string, string>>? StageVariables { get; init; }
    public bool? IsDraft { get; init; }
    public string[]? AttachmentIds { get; init; }
    public string? WorkflowProfileId { get; init; }
    public string? RepositoryName { get; init; }
    public int? ParentIssueNumber { get; init; }

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
        if (raw.TryGetProperty("repositoryName", out _)) fields.Add(nameof(RepositoryName));
        if (raw.TryGetProperty("parentIssueNumber", out _)) fields.Add(nameof(ParentIssueNumber));

        return new UpdateIssueRequest
        {
            Title = GetString(raw, "title"),
            Body = GetString(raw, "body"),
            Labels = GetStringMap(raw, "labels"),
            Priority = GetString(raw, "priority"),
            Model = GetString(raw, "model"),
            ModelVariant = GetString(raw, "modelVariant"),
            StageModels = GetStringMap(raw, "stageModels"),
            StageModelVariants = GetStringMap(raw, "stageModelVariants"),
            StageVariables = GetNestedStringMap(raw, "stageVariables"),
            IsDraft = GetNullableBool(raw, "isDraft"),
            AttachmentIds = GetStringArray(raw, "attachmentIds"),
            WorkflowProfileId = GetString(raw, "workflowProfileId"),
            RepositoryName = GetString(raw, "repositoryName"),
            ParentIssueNumber = GetNullableInt(raw, "parentIssueNumber"),
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

    private static int? GetNullableInt(JsonElement raw, string name) =>
        raw.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var value)
            ? value
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

public record AddCommentRequest(string Author, string Body, string[]? AttachmentIds = null);

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
    string? SourceTemplateId,
    bool HasCustomTemplate,
    string? Yaml,
    string? WorkflowRunId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ProfileId,
    string UpdateMode,
    VariableBundle Variables,
    string UpdatedAt,
    string TemplateSource);

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
/// <c>CurrentTotal</c> and <c>PreviousTotal</c> are strictly additive:
/// the pre-existing <c>Buckets</c> / <c>Window</c> series and the
/// fixed day/week granularity are unchanged. The two totals are
/// computed from the same latest-terminal-event classification the
/// per-bucket series uses, over the current window
/// <c>[now − W, now]</c> and the immediately-preceding window of the
/// same length <c>[now − 2W, now − W]</c>. <see cref="CompletionMetricsTotalsDto.SampleCount"/>
/// is the terminal-issue count in the window — it lets the caller
/// distinguish a zero-sample (empty) window (<c>SampleCount == 0</c>,
/// no terminal issues fell in the window) from a genuine
/// zero-completion window (<c>SampleCount > 0</c>, every terminal
/// issue cancelled and none completed).
/// </summary>
public sealed record CompletionMetricsResponse(
    string Bucket,
    CompletionMetricsWindowDto Window,
    CompletionMetricsBucketDto[] Buckets,
    CompletionMetricsTotalsDto CurrentTotal,
    CompletionMetricsTotalsDto PreviousTotal);

/// <summary>
/// Window-scoped completion totals. <see cref="Completed"/> and
/// <see cref="Failed"/> aggregate the latest-terminal-event
/// classification across every issue whose terminal event falls in
/// the window. <see cref="SampleCount"/> is the number of terminal
/// issues contributing to the totals in the window — it is the
/// discriminator that distinguishes the empty (zero-sample) result
/// (<c>SampleCount == 0</c>) from a genuine zero-completion window
/// (<c>SampleCount > 0</c>, <c>Completed == 0</c>).
/// </summary>
public sealed record CompletionMetricsTotalsDto(
    int Completed,
    int Failed,
    int SampleCount);

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
/// Response shape for the AI quality metrics endpoint. A single
/// range-driven primary window is returned, replacing the prior
/// dual-window (fixed 7d + range-driven 30d) contract. The primary
/// window's span follows the page-level <c>range</c> query parameter
/// (<c>7d</c>/<c>30d</c>/<c>90d</c>, default <c>30d</c>); the field
/// name <see cref="Window"/> carries no fixed-day-count implication.
/// <see cref="Trend"/> is a strictly additive pre-sized per-day series
/// across the same span as <see cref="Window"/>.
/// <para>
/// <see cref="PreviousFirstTimeRightRate"/> and <see cref="PreviousSampleCount"/>
/// are strictly additive: they carry the first-time-right rate over the
/// immediately-preceding window of the same length as <see cref="Window"/>,
/// using the identical ship-time windowing and FTR classification.
/// <see cref="PreviousSampleCount"/> is the empty discriminator — when it
/// is <c>0</c>, the previous window is empty (no shipped issues fell in it)
/// and <see cref="PreviousFirstTimeRightRate"/> is <c>null</c>, structurally
/// distinguishable from a genuine <c>0</c> or <c>1</c> rate. The two
/// windows are evaluated independently: the current window can be non-empty
/// while the previous window is empty and vice-versa.
/// </para>
/// </summary>
public sealed record QualityMetricsResponse(
    QualityMetricsWindowDto Window,
    double? PreviousFirstTimeRightRate,
    int PreviousSampleCount,
    QualityTrendDto Trend);

/// <summary>
/// Per-day quality trend over the trailing window. <see cref="Points"/>
/// is dense — every day in the window is emitted, with null rates for
/// buckets that contain no shipped issues. The window span matches the
/// range-driven primary <see cref="QualityMetricsResponse.Window"/>.
/// </summary>
public sealed record QualityTrendDto(
    string Bucket,
    string From,
    string To,
    QualityTrendPointDto[] Points);

/// <summary>
/// One per-day bucket in the quality trend. <see cref="Boundary"/> is the
/// ISO calendar day (yyyy-MM-dd, UTC). <see cref="SampleCount"/> is the
/// number of shipped-in-bucket issues; a zero sample count yields null
/// rates, distinguishable from a genuine zero-or-one rate.
/// </summary>
public sealed record QualityTrendPointDto(
    string Boundary,
    int SampleCount,
    double? FirstTimeRightRate,
    double? ReworkRate);

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

/// <summary>
/// One per-issue sample in the delivery-time series. <see cref="IssueNumber"/>
/// is the project's display number. <see cref="CompletedAt"/> is the ISO-8601
/// formatted completion moment. <see cref="LeadDays"/> is always defined
/// (<c>CompletedAt − CreatedAt</c>). <see cref="CycleDays"/> is the
/// earliest-<c>IssueWorkStarted</c> to <c>CompletedAt</c> duration when at
/// least one work-start event exists for the issue, or <c>null</c> when the
/// issue has no recorded work-start (the <c>null</c> value is the "undefined"
/// marker, structurally distinguishable from a genuine zero-duration cycle).
/// </summary>
public sealed record DeliveryTimePointDto(
    int IssueNumber,
    string CompletedAt,
    double LeadDays,
    double? CycleDays);

/// <summary>
/// Response shape for the delivery-time metrics endpoint. The endpoint
/// returns one entry per delivered issue in the fixed 30-day trailing
/// window anchored on completion time. <see cref="Points"/> is empty
/// (not an error, not a fabricated zero) when no delivered issues fall
/// in the window. <see cref="PreviousCycleDays"/> is strictly additive:
/// the existing <see cref="Points"/> series and the existing fixed
/// trailing window are preserved unchanged; only the previous-window
/// average is added.
/// </summary>
public sealed record DeliveryTimeMetricsResponse(
    DeliveryTimePointDto[] Points,
    double? PreviousCycleDays);

/// <summary>
/// Response shape for the stage-duration metrics endpoint. The stages are
/// returned in workflow stage order. Stages reached by no delivered issue
/// in the window are absent (a fabricated zero would mislead the consuming
/// chart). <see cref="FlowEfficiencyRatio"/> is <c>null</c> for the empty
/// result so it is distinguishable from a genuine zero-or-one ratio.
/// <see cref="WaitBreakout"/> averages are <c>null</c> for the empty
/// result, distinguishable from a genuine zero-wait issue.
/// </summary>
public sealed record StageDurationMetricsResponse(
    StageDurationMetricsWindowDto Window,
    StageDurationStageDto[] Stages,
    double? FlowEfficiencyRatio,
    StageDurationWaitBreakoutDto? WaitBreakout);

public sealed record StageDurationMetricsWindowDto(
    string From,
    string To);

/// <summary>
/// One per-stage aggregate in the stage-duration response.
/// <see cref="AverageSeconds"/> and <see cref="MedianSeconds"/> are the
/// arithmetic mean and median of the latest-attempt durations across the
/// windowed delivered issues. <see cref="SampleCount"/> is the number of
/// issues contributing a defined sample (started-and-completed latest
/// attempt) for the stage — distinguishes a true zero-sample window from
/// a stage with a genuine zero-duration sample.
/// </summary>
public sealed record StageDurationStageDto(
    string Stage,
    int SampleCount,
    double? AverageSeconds,
    double? MedianSeconds);

/// <summary>
/// The wait-breakout averages alongside the flow-efficiency ratio.
/// <see cref="AverageApprovalGateWaitSeconds"/> is the mean of the
/// per-issue approval-gate wait over the issues contributing to the
/// ratio. <see cref="AverageInactiveGapSeconds"/> is the mean of
/// <c>cycle − Σ(stage durations)</c> over the same population. Both
/// fields are <c>null</c> when no delivered issue in the window has a
/// defined cycle time; zero-wait issues contribute zero rather than
/// exclusion.
/// </summary>
public sealed record StageDurationWaitBreakoutDto(
    double? AverageApprovalGateWaitSeconds,
    double? AverageInactiveGapSeconds);
