namespace Mohist.Server.Sessions.Domain;

public static partial class AgentSessionExtensions
{
    extension(AgentSession session)
    {
        public bool IsTerminal => session.Status.Phase is AgentSessionStatus.Completed or AgentSessionStatus.Failed or AgentSessionStatus.Cancelled;

        public bool IsCreated => session.Status.Phase == AgentSessionStatus.Created;

        public void MergeContext(
            string? runnerId,
            string? workId,
            string? workType,
            string? stage,
            string? title,
            int? issueNumber)
        {
            _ = runnerId;
            _ = workType;
            _ = stage;
            _ = title;
            _ = workId;
            if (session.IssueNumber == 0 && issueNumber is > 0)
                session.Metadata = session.Metadata.WithLabel(AgentSessionMetadataKeys.IssueNumber, issueNumber.Value.ToString());
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
            _ = changeDir;
            _ = processPid;

            session.Runtime = session.Runtime with
            {
                WorkDir = session.Runtime.WorkDir ?? workDir
            };
            session.Status = session.Status with { AgentRuntimeSessionId = agentSessionId };
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
            session.Settings = session.Settings with { Model = model ?? session.Settings.Model };
            session.Status = session.Status with
            {
                Phase = AgentSessionStatus.Running,
                StartedAt = session.Status.StartedAt ?? now,
                LastDataAt = now
            };
        }

        public void RecordActivity(DateTime now)
        {
            if (session.IsTerminal) return;
            session.Status = session.Status with { LastDataAt = now };
        }

        public void MarkActive(string status, DateTime now, string? failureReason = null)
        {
            if (session.IsTerminal) return;
            session.Status = session.Status with
            {
                Phase = AgentSessionStatusNames.ParseActive(status),
                LastDataAt = now,
                FailureReason = failureReason ?? session.Status.FailureReason
            };
        }

        public void Complete(DateTime now, int? exitCode)
        {
            if (session.IsTerminal) return;
            session.Status = session.Status with
            {
                Phase = AgentSessionStatus.Completed,
                CompletedAt = now,
                LastDataAt = now,
                ExitCode = exitCode ?? session.Status.ExitCode,
                FailureReason = null
            };
        }

        public void Fail(DateTime now, string? reason, int? exitCode = null)
        {
            if (session.IsTerminal) return;
            session.Status = session.Status with
            {
                Phase = AgentSessionStatus.Failed,
                CompletedAt = now,
                LastDataAt = now,
                FailureReason = reason ?? session.Status.FailureReason,
                ExitCode = exitCode ?? session.Status.ExitCode
            };
        }

        public void Cancel(DateTime now, string? reason, int? exitCode = null)
        {
            if (session.IsTerminal) return;
            session.Status = session.Status with
            {
                Phase = AgentSessionStatus.Cancelled,
                CompletedAt = now,
                LastDataAt = now,
                FailureReason = reason ?? session.Status.FailureReason,
                ExitCode = exitCode ?? session.Status.ExitCode
            };
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

            var usage = session.Status.UsageSummary ?? new AgentUsageSummary();
            session.Status = session.Status with
            {
                UsageSummary = usage with
                {
                    InputTokens = AddNonNegative(usage.InputTokens, inputTokens),
                    OutputTokens = AddNonNegative(usage.OutputTokens, outputTokens),
                    TotalTokens = AddNonNegative(usage.TotalTokens, totalTokens),
                    CachedReadTokens = AddNonNegative(usage.CachedReadTokens, cachedReadTokens),
                    ThoughtTokens = AddNonNegative(usage.ThoughtTokens, thoughtTokens),
                    CostAmount = AddNonNegative(usage.CostAmount, costAmount),
                    CostCurrency = costCurrency ?? usage.CostCurrency,
                    ContextWindowUsed = contextWindowUsed ?? usage.ContextWindowUsed,
                    ContextWindowSize = contextWindowSize ?? usage.ContextWindowSize
                }
            };
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
