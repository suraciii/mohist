namespace Mohist.Server.Sessions.Domain;

public static partial class AgentSessionExtensions
{
    extension(AgentSession session)
    {
        public bool IsTerminal => session.Status.Phase is AgentSessionStatus.Completed or AgentSessionStatus.Failed or AgentSessionStatus.Cancelled;

        public bool IsCreated => session.Status.Phase == AgentSessionStatus.Created;

        public IReadOnlyList<AgentSessionEvent> MergeContext(
            string? runnerId,
            string? workId,
            string? workType,
            string? stage,
            string? title,
            int? issueNumber)
        {
            _ = runnerId;
            if (session.IssueNumber == 0 && issueNumber is > 0)
                session.Metadata = session.Metadata.WithLabel(AgentSessionMetadataKeys.IssueNumber, issueNumber.Value.ToString());
            session.Metadata = session.Metadata
                .WithLabel(AgentSessionMetadataKeys.WorkId, workId)
                .WithLabel(AgentSessionMetadataKeys.WorkType, workType)
                .WithLabel(AgentSessionMetadataKeys.Stage, stage)
                .WithAnnotation(AgentSessionMetadataKeys.Title, title);
            return [];
        }

        public IReadOnlyList<AgentSessionEvent> AttachAgent(
            string agentSessionId,
            string? model,
            string? workDir,
            string? changeDir,
            int? processPid,
            DateTime now)
        {
            if (session.IsTerminal) return [];
            _ = changeDir;
            _ = processPid;
            var oldModel = session.Settings.Model;
            var existingAgentSessionId = session.Status.AgentRuntimeSessionId;
            if (!string.IsNullOrWhiteSpace(existingAgentSessionId)
                && !string.Equals(existingAgentSessionId, agentSessionId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Agent session {session.Id} is already attached to physical ACP session {existingAgentSessionId}; cannot attach {agentSessionId}.");

            session.Runtime = session.Runtime with
            {
                WorkDir = session.Runtime.WorkDir ?? workDir
            };
            session.Status = session.Status with { AgentRuntimeSessionId = existingAgentSessionId ?? agentSessionId };
            session.Start(model, now);
            var events = new List<AgentSessionEvent>();
            if (string.IsNullOrWhiteSpace(existingAgentSessionId))
                events.Add(new AgentSessionStarted(agentSessionId));
            if (!string.Equals(oldModel, session.Settings.Model, StringComparison.Ordinal))
                events.Add(new AgentSessionModelChanged(session.Settings.Model));
            return events;
        }

        public IReadOnlyList<AgentSessionEvent> EnsureActive(DateTime now)
        {
            if (session.IsCreated)
                return session.MarkActive("running", now);
            return [];
        }

        public IReadOnlyList<AgentSessionEvent> Start(string? model, DateTime now)
        {
            if (session.IsTerminal) return [];
            var oldModel = session.Settings.Model;
            session.Settings = session.Settings with { Model = model ?? session.Settings.Model };
            session.Status = session.Status with
            {
                Phase = AgentSessionStatus.Running,
                StartedAt = session.Status.StartedAt ?? now,
                LastDataAt = now
            };
            return !string.Equals(oldModel, session.Settings.Model, StringComparison.Ordinal)
                ? [new AgentSessionModelChanged(session.Settings.Model)]
                : [];
        }

        public IReadOnlyList<AgentSessionEvent> RecordActivity(DateTime now)
        {
            if (session.IsTerminal) return [];
            session.Status = session.Status with { LastDataAt = now };
            return [];
        }

        public IReadOnlyList<AgentSessionEvent> MarkActive(string status, DateTime now, string? failureReason = null)
        {
            if (session.IsTerminal) return [];
            var phase = AgentSessionStatusNames.ParseActive(status);
            var changed = session.Status.Phase != phase;
            session.Status = session.Status with
            {
                Phase = phase,
                LastDataAt = now,
                FailureReason = failureReason ?? session.Status.FailureReason
            };
            return changed ? [new AgentSessionActivated(AgentSessionStatusNames.ToName(phase))] : [];
        }

        public IReadOnlyList<AgentSessionEvent> Complete(DateTime now, int? exitCode)
        {
            if (session.IsTerminal) return [];
            session.Status = session.Status with
            {
                Phase = AgentSessionStatus.Completed,
                CompletedAt = now,
                LastDataAt = now,
                ExitCode = exitCode ?? session.Status.ExitCode,
                FailureReason = null
            };
            return [new AgentSessionCompleted(session.Status.ExitCode)];
        }

        public IReadOnlyList<AgentSessionEvent> Fail(DateTime now, string? reason, int? exitCode = null)
        {
            if (session.IsTerminal) return [];
            session.Status = session.Status with
            {
                Phase = AgentSessionStatus.Failed,
                CompletedAt = now,
                LastDataAt = now,
                FailureReason = reason ?? session.Status.FailureReason,
                ExitCode = exitCode ?? session.Status.ExitCode
            };
            return [new AgentSessionFailed(session.Status.FailureReason, session.Status.ExitCode)];
        }

        public IReadOnlyList<AgentSessionEvent> Cancel(DateTime now, string? reason, int? exitCode = null)
        {
            if (session.IsTerminal) return [];
            session.Status = session.Status with
            {
                Phase = AgentSessionStatus.Cancelled,
                CompletedAt = now,
                LastDataAt = now,
                FailureReason = reason ?? session.Status.FailureReason,
                ExitCode = exitCode ?? session.Status.ExitCode
            };
            return [new AgentSessionCancelled(session.Status.FailureReason, session.Status.ExitCode)];
        }

        public IReadOnlyList<AgentSessionEvent> ApplyUsage(
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
            if (session.IsTerminal) return [];

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
            return [new AgentSessionUsageRecorded(session.Status.UsageSummary ?? new AgentUsageSummary())];
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
