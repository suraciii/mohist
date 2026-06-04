namespace Mohist.Server.Sessions.Domain;

public static partial class WorkflowAgentSessionExtensions
{
    extension(WorkflowAgentSession session)
    {
        public bool IsTerminal => session.Status is AgentSessionStatus.Completed or AgentSessionStatus.Failed or AgentSessionStatus.Cancelled;

        public bool IsCreated => session.Status == AgentSessionStatus.Created;

        public void MergeContext(
            string? runnerId,
            string? workId,
            string? workType,
            string? stage,
            string? title,
            int? issueNumber)
        {
            if (runnerId is not null)
                session.RunnerId = runnerId;
            session.WorkId ??= workId;
            session.WorkType ??= workType;
            session.Stage ??= stage;
            session.Title ??= title;
            if (session.IssueNumber == 0 && issueNumber is > 0)
                session.IssueNumber = issueNumber.Value;
        }

        public void StartNewWork(
            string? runnerId,
            string? workId,
            string? workType,
            string? stage,
            string? title,
            int? issueNumber,
            DateTime now)
        {
            session.RunnerId = runnerId ?? session.RunnerId;
            session.WorkId = workId;
            session.WorkType = workType;
            session.Stage = stage;
            session.Title = title;
            if (session.IssueNumber == 0 && issueNumber is > 0)
                session.IssueNumber = issueNumber.Value;

            session.Status = AgentSessionStatus.Created;
            session.StartedAt = null;
            session.CompletedAt = null;
            session.FailureReason = null;
            session.ExitCode = null;
            session.LastDataAt = now;
            session.LastHeartbeatAt = now;
        }

        public bool AttachAgent(
            string agentSessionId,
            string? model,
            string? workDir,
            string? changeDir,
            int? processPid,
            DateTime now)
        {
            if (session.IsTerminal) return false;

            session.AgentSessionId = agentSessionId;
            session.Model = model ?? session.Model;
            session.WorkDir = workDir ?? session.WorkDir;
            session.ChangeDir = changeDir ?? session.ChangeDir;
            session.ProcessPid = processPid ?? session.ProcessPid;
            session.Start(model, now);
            return true;
        }

        public void EnsureActive(DateTime now)
        {
            if (session.IsCreated)
                session.MarkActive("running", now);
        }

        public void Start(string? model, DateTime now)
        {
            if (session.IsTerminal) return;
            session.Status = AgentSessionStatus.Running;
            session.Model = model ?? session.Model;
            session.StartedAt ??= now;
            session.LastDataAt = now;
            session.LastHeartbeatAt = now;
        }

        public void RecordActivity(DateTime now)
        {
            if (session.IsTerminal) return;
            session.LastDataAt = now;
            session.LastHeartbeatAt = now;
        }

        public void MarkActive(string status, DateTime now, string? failureReason = null)
        {
            if (session.IsTerminal) return;
            session.Status = AgentSessionStatusNames.ParseActive(status);
            session.LastDataAt = now;
            session.LastHeartbeatAt = now;
            session.FailureReason = failureReason ?? session.FailureReason;
        }

        public void Complete(DateTime now, int? exitCode)
        {
            if (session.IsTerminal) return;
            session.Status = AgentSessionStatus.Completed;
            session.CompletedAt = now;
            session.LastDataAt = now;
            session.LastHeartbeatAt = now;
            session.ExitCode = exitCode ?? session.ExitCode;
            session.FailureReason = null;
            session.FailureCategory = null;
        }

        public void Fail(DateTime now, string? reason, int? exitCode = null, string? failureCategory = null)
        {
            if (session.IsTerminal) return;
            session.Status = AgentSessionStatus.Failed;
            session.CompletedAt = now;
            session.LastDataAt = now;
            session.LastHeartbeatAt = now;
            session.FailureReason = reason ?? session.FailureReason;
            session.FailureCategory = failureCategory ?? session.FailureCategory;
            session.ExitCode = exitCode ?? session.ExitCode;
        }

        public void Cancel(DateTime now, string? reason, int? exitCode = null, string? failureCategory = null)
        {
            if (session.IsTerminal) return;
            session.Status = AgentSessionStatus.Cancelled;
            session.CompletedAt = now;
            session.LastDataAt = now;
            session.LastHeartbeatAt = now;
            session.FailureReason = reason ?? session.FailureReason;
            session.FailureCategory = failureCategory ?? session.FailureCategory;
            session.ExitCode = exitCode ?? session.ExitCode;
        }

        public void UpdateResolvedModel(string? model)
        {
            if (model is not null)
                session.ResolvedModel = model;
        }

        public void ApplyUsage(
            long? inputTokens,
            long? outputTokens,
            long? totalTokens,
            long? cachedReadTokens,
            long? thoughtTokens,
            double? costAmount,
            string? costCurrency,
            long? contextWindowUsed,
            long? contextWindowSize)
        {
            if (session.IsTerminal) return;

            session.InputTokens = AddNonNegative(session.InputTokens, inputTokens);
            session.OutputTokens = AddNonNegative(session.OutputTokens, outputTokens);
            session.TotalTokens = AddNonNegative(session.TotalTokens, totalTokens);
            session.CachedReadTokens = AddNonNegative(session.CachedReadTokens, cachedReadTokens);
            session.ThoughtTokens = AddNonNegative(session.ThoughtTokens, thoughtTokens);
            session.CostAmount = AddNonNegative(session.CostAmount, costAmount);
            if (costCurrency is not null)
                session.CostCurrency = costCurrency;
            if (contextWindowUsed is not null)
                session.ContextWindowUsed = contextWindowUsed;
            if (contextWindowSize is not null)
                session.ContextWindowSize = contextWindowSize;
        }

        public void RecordToolCall(bool isError)
        {
            if (session.IsTerminal) return;

            session.ToolCallCount = (session.ToolCallCount ?? 0) + 1;
            if (isError)
            {
                var errors = (session.ToolErrorCount ?? 0) + 1;
                session.ToolErrorCount = Math.Min(errors, session.ToolCallCount.Value);
            }
        }

        public void RecordToolError()
        {
            if (session.IsTerminal) return;

            var errors = (session.ToolErrorCount ?? 0) + 1;
            var calls = session.ToolCallCount ?? 0;
            session.ToolErrorCount = Math.Min(errors, calls);
        }

        private static long? AddNonNegative(long? current, long? delta)
        {
            if (delta is null or < 0) return current;
            return (current ?? 0) + delta.Value;
        }

        private static double? AddNonNegative(double? current, double? delta)
        {
            if (delta is null or < 0) return current;
            return (current ?? 0) + delta.Value;
        }
    }
}
