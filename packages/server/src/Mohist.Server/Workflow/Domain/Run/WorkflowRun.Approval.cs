using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    public const string DefaultFeedbackTaskId = "apply-feedback";
    public const string DefaultFeedbackTaskTitle = "Apply approval feedback";
    public const string DefaultFeedbackTaskUses = "mohist/opencode";
    public const int FeedbackSummaryMaxLength = 100;
    private const string Ellipsis = "\u2026";

    public static string BuildFeedbackSummary(string? body)
    {
        if (string.IsNullOrEmpty(body)) return string.Empty;
        if (body.Length <= FeedbackSummaryMaxLength) return body;
        return body[..(FeedbackSummaryMaxLength - 1)] + Ellipsis;
    }

    /// <summary>
    /// Extracts the agent-written resolution summary from task output.
    /// Strips the "## Feedback Resolution" header and the trailing
    /// "## Verification" section, then trims surrounding whitespace.
    /// </summary>
    public static string? ExtractResolutionSummary(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var text = output.Trim();

        const string resolutionHeader = "## Feedback Resolution";
        const string verificationHeader = "## Verification";

        if (text.StartsWith(resolutionHeader, StringComparison.Ordinal))
        {
            text = text[resolutionHeader.Length..].TrimStart('\r', '\n', ' ');
        }

        var verificationIdx = text.IndexOf(verificationHeader, StringComparison.Ordinal);
        if (verificationIdx >= 0)
        {
            text = text[..verificationIdx].TrimEnd('\r', '\n', ' ');
        }

        return text.Length == 0 ? null : text;
    }

    extension(WorkflowRun)
    {
        public static IReadOnlyList<TaskDefinition> BuildDefaultFeedbackTasks(string stage) => [BuildDefaultFeedbackTask(stage)];

        private static TaskDefinition BuildDefaultFeedbackTask(string stage)
        {
            var withInput = new Dictionary<string, JsonElement?>
            {
                ["session"] = JSON.SerializeToElement(stage),
                ["prompt"] = JSON.SerializeToElement($"${{{{ prompts.{DefaultFeedbackTaskId} }}}}"),
                ["options"] = JSON.SerializeToElement("${{ vars.agent }}"),
            };
            return new TaskDefinition(
                Id: DefaultFeedbackTaskId,
                Title: DefaultFeedbackTaskTitle,
                Uses: DefaultFeedbackTaskUses,
                With: withInput);
        }

        public static IReadOnlyList<TaskDefinition> ResolveFeedbackTasks(IReadOnlyList<TaskDefinition>? configs, string stage)
        {
            if (configs is null || configs.Count == 0)
                return BuildDefaultFeedbackTasks(stage);

            return configs.Select(config => NormalizeFeedbackTask(config, stage)).ToList();
        }

        private static TaskDefinition NormalizeFeedbackTask(TaskDefinition config, string stage)
        {
            if (!string.Equals(config.Uses, "mohist/opencode", StringComparison.Ordinal)
                && !string.Equals(config.Uses, "mohist/acp-agent", StringComparison.Ordinal))
                return config;
            var with = config.With;
            if (with is null || !with.ContainsKey("session"))
            {
                with = with is null
                    ? new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                    : new Dictionary<string, JsonElement?>(with, StringComparer.Ordinal);
                with["session"] = JSON.SerializeToElement(stage);
            }
            return new TaskDefinition(
                config.Id,
                config.Title,
                config.Uses,
                with,
                config.Expect,
                config.Artifacts,
                config.SetVars,
                config.Recovery);
        }
    }

    extension(WorkflowRun run)
    {
        public IReadOnlyList<WorkflowEvent> Approve(DateTimeOffset now, string decidedBy)
        {
            var current = run.CurrentStage();
            if (!current.IsAwaitingApproval)
                throw new InvalidOperationException($"Stage {current.Id} is not awaiting approval");

            var stageId = current.Id;
            current.ApprovalStatus = new ApprovalStatus(
                "approved",
                current.ApprovalStatus!.RequestedAt,
                now.ToString("O"),
                decidedBy);
            current.Status = StageRunStatus.Completed;
            var events = new List<WorkflowEvent>
            {
                new StageApprovalResolved(stageId, ApprovalResult.Approved, DecidedBy: decidedBy)
            };
            events.AddRange(run.Advance(now));
            return events;
        }

        public IReadOnlyList<WorkflowEvent> Reject(string? reason, DateTimeOffset now, string decidedBy)
        {
            var current = run.CurrentStage();
            if (!current.IsAwaitingApproval)
                throw new InvalidOperationException($"Stage {current.Id} is not awaiting approval");

            current.ApprovalStatus = new ApprovalStatus(
                "rejected",
                current.ApprovalStatus!.RequestedAt,
                now.ToString("O"),
                decidedBy);
            current.Failure = new FailureDetails(FailureReason.ApprovalRejected, current.Id, Message: reason);
            run.Failure = current.Failure;
            current.Status = StageRunStatus.Failed;
            run.Status = WorkflowRunStatus.Failed;
            return [
                new StageApprovalResolved(current.Id, ApprovalResult.Rejected, reason, decidedBy),
                new StageFailed(current.Id, reason),
                new WorkflowRunFailed(reason)
            ];
        }

        public IReadOnlyList<WorkflowEvent> RequestChanges(
            string body,
            string feedbackId,
            DateTimeOffset now,
            string decidedBy,
            IReadOnlyList<TaskDefinition>? feedbackTasks = null)
        {
            if (string.IsNullOrWhiteSpace(body))
                throw new ArgumentException("Feedback body is required", nameof(body));

            var current = run.CurrentStage();
            if (!current.IsAwaitingApproval)
                throw new InvalidOperationException($"Stage {current.Id} is not awaiting approval");

            if (!current.Initialized)
                throw new InvalidOperationException($"Cannot request changes: stage {current.Id} is not initialized");

            var existingApproval = current.ApprovalStatus!;
            current.ApprovalStatus = existingApproval with
            {
                DecidedBy = decidedBy,
                RespondedAt = now.ToString("O"),
            };

            var feedback = new ApprovalFeedback(
                Id: feedbackId,
                WorkflowRunId: run.Id,
                Stage: current.Id,
                Body: body,
                Status: ApprovalFeedbackStatus.Open,
                CreatedAt: now);

            run.Feedback.Add(feedback);

            var resolvedTasks = feedbackTasks ?? WorkflowRunExtensions.BuildDefaultFeedbackTasks(current.Id);
            if (resolvedTasks.Count == 0)
                throw new ArgumentException("Feedback requires at least one task", nameof(feedbackTasks));

            var events = new List<WorkflowEvent>();

            var runtimeEvents = run.AddRuntimeTasks(
                resolvedTasks,
                now,
                stage: current.Id,
                invalidateChecks: true,
                causedByFeedbackId: feedbackId);
            events.AddRange(runtimeEvents);

            events.Add(new FeedbackRequested(current.Id, feedbackId, body));
            events.Add(new StageApprovalResolved(current.Id, ApprovalResult.Rejected, body, decidedBy));
            return events;
        }

        public ApprovalFeedback? ResolveFeedback(string feedbackId, string taskId, JsonElement? output, DateTimeOffset now)
        {
            var feedback = run.Feedback.FirstOrDefault(f => f.Id == feedbackId);
            if (feedback is null) return null;
            if (feedback.Status == ApprovalFeedbackStatus.Resolved) return feedback;

            var feedbackTasks = run.CurrentStage().Tasks
                .Where(task => task.CausedByFeedbackId == feedbackId)
                .ToList();
            if (feedbackTasks.Count == 0 || feedbackTasks.Any(task => task.Status != TaskRunStatus.Completed))
                return null;

            var summary = ResolveFeedbackSummary(feedbackTasks, output);

            var resolved = feedback with
            {
                Status = ApprovalFeedbackStatus.Resolved,
                ResolutionTaskId = taskId,
                ResolvedAt = now,
                ResolutionSummary = ExtractResolutionSummary(summary),
            };

            var idx = run.Feedback.FindIndex(f => f.Id == feedbackId);
            if (idx >= 0) run.Feedback[idx] = resolved;
            return resolved;
        }
    }

    /// <summary>
    /// Derive the feedback resolution summary input string from completed
    /// feedback tasks. Only <c>core/process</c> output is interpreted as
    /// text via its explicit <c>output.stdout</c> adapter; every other
    /// Action produces a <c>null</c> candidate. The first non-null
    /// candidate wins. A fallback <paramref name="output"/> string (from
    /// the inbound report) is reserved for historical compat and never
    /// JSON-serializes an arbitrary object into summary text.
    /// </summary>
    private static string? ResolveFeedbackSummary(IReadOnlyList<TaskRun> feedbackTasks, JsonElement? reportOutput)
    {
        foreach (var task in feedbackTasks)
        {
            var uses = task.Uses?.Trim().ToLowerInvariant();
            if (uses != "core/process") continue;
            if (!task.Output.HasValue) continue;
            var output = task.Output.Value;
            if (output.ValueKind != JsonValueKind.Object) continue;
            if (!output.TryGetProperty("stdout", out var stdoutElement)) continue;
            if (stdoutElement.ValueKind != JsonValueKind.String) continue;
            return stdoutElement.GetString();
        }

        if (reportOutput.HasValue && reportOutput.Value.ValueKind == JsonValueKind.String)
            return reportOutput.Value.GetString();

        return null;
    }
}
