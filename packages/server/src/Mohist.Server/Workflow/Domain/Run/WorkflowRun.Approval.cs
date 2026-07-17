using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;

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

    public static string BuildFeedbackShowCommand(
        string? issueNumber,
        string feedbackId,
        string? projectId)
    {
        var number = string.IsNullOrWhiteSpace(issueNumber) ? "<number>" : issueNumber;
        var proj = string.IsNullOrWhiteSpace(projectId) ? "<project-id>" : projectId;
        return $"mo issue feedback show {number} --feedback {feedbackId} --project-id {proj} --output json";
    }

    public static string BuildFeedbackShowCommand(
        int? issueNumber,
        string feedbackId,
        string? projectId)
        => BuildFeedbackShowCommand(issueNumber?.ToString(), feedbackId, projectId);

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
        public static TaskDefinition BuildDefaultFeedbackTask(string stage)
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

        public static TaskDefinition ResolveFeedbackTask(TaskDefinition? config, string stage)
        {
            if (config is null)
                return BuildDefaultFeedbackTask(stage);

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
        public IReadOnlyList<WorkflowEvent> Approve(DateTimeOffset now)
        {
            var current = run.CurrentStage();
            if (!current.IsAwaitingApproval)
                throw new InvalidOperationException($"Stage {current.Id} is not awaiting approval");

            var stageId = current.Id;
            current.ApprovalStatus = new ApprovalStatus(
                "approved",
                current.ApprovalStatus!.RequestedAt,
                now.ToString("O"));
            current.Status = StageRunStatus.Completed;
            var events = new List<WorkflowEvent>
            {
                new StageApprovalResolved(stageId, ApprovalResult.Approved)
            };
            events.AddRange(run.Advance(now));
            return events;
        }

        public IReadOnlyList<WorkflowEvent> Reject(string? reason, DateTimeOffset now)
        {
            var current = run.CurrentStage();
            if (!current.IsAwaitingApproval)
                throw new InvalidOperationException($"Stage {current.Id} is not awaiting approval");

            current.ApprovalStatus = new ApprovalStatus(
                "rejected",
                current.ApprovalStatus!.RequestedAt,
                now.ToString("O"));
            current.Failure = new FailureDetails(FailureReason.ApprovalRejected, current.Id, Message: reason);
            run.Failure = current.Failure;
            current.Status = StageRunStatus.Failed;
            run.Status = WorkflowRunStatus.Failed;
            return [
                new StageApprovalResolved(current.Id, ApprovalResult.Rejected, reason),
                new StageFailed(current.Id, reason),
                new WorkflowRunFailed(reason)
            ];
        }

        public IReadOnlyList<WorkflowEvent> RequestChanges(
            string body,
            string feedbackId,
            DateTimeOffset now,
            TaskDefinition? feedbackTask = null)
        {
            if (string.IsNullOrWhiteSpace(body))
                throw new ArgumentException("Feedback body is required", nameof(body));

            var current = run.CurrentStage();
            if (!current.IsAwaitingApproval)
                throw new InvalidOperationException($"Stage {current.Id} is not awaiting approval");

            if (!current.Initialized)
                throw new InvalidOperationException($"Cannot request changes: stage {current.Id} is not initialized");

            var feedback = new ApprovalFeedback(
                Id: feedbackId,
                WorkflowRunId: run.Id,
                Stage: current.Id,
                Body: body,
                Status: ApprovalFeedbackStatus.Open,
                CreatedAt: now);

            run.Feedback.Add(feedback);

            var resolvedTask = feedbackTask ?? WorkflowRunExtensions.BuildDefaultFeedbackTask(current.Id);

            var events = new List<WorkflowEvent>();

            var runtimeEvents = run.AddRuntimeTask(
                resolvedTask,
                now,
                stage: current.Id,
                invalidateChecks: true,
                causedByFeedbackId: feedbackId);
            events.AddRange(runtimeEvents);

            events.Add(new FeedbackRequested(current.Id, feedbackId, body));
            return events;
        }

        public ApprovalFeedback? ResolveFeedback(string feedbackId, string taskId, string? output, DateTimeOffset now)
        {
            var feedback = run.Feedback.FirstOrDefault(f => f.Id == feedbackId);
            if (feedback is null) return null;
            if (feedback.Status == ApprovalFeedbackStatus.Resolved) return feedback;

            var resolved = feedback with
            {
                Status = ApprovalFeedbackStatus.Resolved,
                ResolutionTaskId = taskId,
                ResolvedAt = now,
                ResolutionSummary = ExtractResolutionSummary(output),
            };

            var idx = run.Feedback.FindIndex(f => f.Id == feedbackId);
            if (idx >= 0) run.Feedback[idx] = resolved;
            return resolved;
        }
    }
}
