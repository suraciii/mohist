namespace Mohist.Server.Sessions.Domain;

public static partial class AgentSessionExtensions
{
    extension(AgentSession session)
    {
        public IReadOnlyList<AgentSessionEvent> MergeMetadata(AgentSessionMetadata? metadata)
        {
            session.Metadata = session.Metadata.Merge(metadata);
            return [];
        }

        public IReadOnlyList<AgentSessionEvent> AttachPhysicalSession(
            string agentSessionId,
            string? model,
            string? workDir,
            string? changeDir,
            int? processPid,
            DateTime now)
        {
            _ = changeDir;
            _ = processPid;
            var oldModel = session.Settings.Model;
            var existingAgentSessionId = session.Status.AgentRuntimeSessionId;
            var isNewRuntimeBinding = !string.Equals(existingAgentSessionId, agentSessionId, StringComparison.Ordinal);

            session.Runtime = session.Runtime with
            {
                WorkDir = session.Runtime.WorkDir ?? workDir
            };
            session.Settings = session.Settings with { Model = model ?? session.Settings.Model };
            session.Status = session.Status with
            {
                AgentRuntimeSessionId = isNewRuntimeBinding ? agentSessionId : existingAgentSessionId,
                BoundAt = isNewRuntimeBinding ? now : session.Status.BoundAt ?? now,
                LastDataAt = now,
            };
            var events = new List<AgentSessionEvent>();
            if (isNewRuntimeBinding)
                events.Add(new AgentSessionRuntimeBound(agentSessionId));
            if (!string.Equals(oldModel, session.Settings.Model, StringComparison.Ordinal))
                events.Add(new AgentSessionModelChanged(session.Settings.Model));
            return events;
        }

        public IReadOnlyList<AgentSessionEvent> ResolveModel(string? model, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(model)) return [];
            var oldModel = session.Settings.Model;
            session.Settings = session.Settings with { Model = model };
            session.Status = session.Status with { LastDataAt = now };
            return !string.Equals(oldModel, session.Settings.Model, StringComparison.Ordinal)
                ? [new AgentSessionModelChanged(session.Settings.Model)]
                : [];
        }

        public IReadOnlyList<AgentSessionEvent> RecordActivity(DateTime now)
        {
            session.Status = session.Status with { LastDataAt = now };
            return [];
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

        public IReadOnlyList<AgentSessionEvent> RebindRuntimeSession(
            string newAgentSessionId,
            long? contextWindowUsedAfter,
            long? contextWindowSizeAfter,
            DateTime now)
        {
            var oldAgentSessionId = session.Status.AgentRuntimeSessionId;
            session.Status = session.Status with
            {
                AgentRuntimeSessionId = newAgentSessionId,
                BoundAt = now,
                LastDataAt = now,
                UsageSummary = (session.Status.UsageSummary ?? new AgentUsageSummary()) with
                {
                    ContextWindowUsed = contextWindowUsedAfter,
                    ContextWindowSize = contextWindowSizeAfter ?? (session.Status.UsageSummary ?? new AgentUsageSummary()).ContextWindowSize,
                }
            };
            var events = new List<AgentSessionEvent>();
            if (!string.Equals(oldAgentSessionId, newAgentSessionId, StringComparison.Ordinal))
                events.Add(new AgentSessionRuntimeBound(newAgentSessionId));
            return events;
        }

        public IReadOnlyList<AgentSessionEvent> RecordCompaction(
            long? contextWindowUsedBefore,
            long? contextWindowUsedAfter,
            long? contextWindowSize,
            string? strategy,
            string? summary,
            DateTime now)
        {
            var usage = session.Status.UsageSummary ?? new AgentUsageSummary();
            session.Status = session.Status with
            {
                LastDataAt = now,
                UsageSummary = usage with
                {
                    ContextWindowUsed = contextWindowUsedAfter ?? usage.ContextWindowUsed,
                    ContextWindowSize = contextWindowSize ?? usage.ContextWindowSize,
                }
            };
            return [new AgentSessionContextCompacted(
                ContextWindowUsedBefore: contextWindowUsedBefore,
                ContextWindowUsedAfter: contextWindowUsedAfter,
                ContextWindowSize: contextWindowSize,
                Strategy: strategy,
                Summary: summary,
                RecordedAt: now)];
        }

        /// <summary>
        /// Records a context-exhaustion classification on the session
        /// after a failed close event. The failureCategory is captured
        /// on the event payload so downstream consumers (UI, retry
        /// guard, analytics) can render a context-exhaustion error
        /// message and decide whether to block retries.
        /// </summary>
        public IReadOnlyList<AgentSessionEvent> RecordContextExhaustion(
            string? failureCategory,
            double? contextUsagePercent,
            long? contextWindowUsed,
            long? contextWindowSize,
            DateTime now)
        {
            session.Status = session.Status with { LastDataAt = now };
            return [new AgentSessionContextExhausted(
                FailureCategory: failureCategory,
                ContextUsagePercent: contextUsagePercent,
                ContextWindowUsed: contextWindowUsed,
                ContextWindowSize: contextWindowSize,
                RecordedAt: now)];
        }

        /// <summary>
        /// Records a context-health transition (green/yellow/red
        /// threshold crossing or large percent change). The session
        /// status is updated so subsequent reads of the session
        /// expose the latest known health snapshot.
        /// </summary>
        public IReadOnlyList<AgentSessionEvent> RecordContextHealthUpdate(
            string healthStatus,
            double? contextUsagePercent,
            long? contextWindowUsed,
            long? contextWindowSize,
            DateTime now)
        {
            session.Status = session.Status with { LastDataAt = now };
            return [new AgentSessionContextHealthUpdated(
                HealthStatus: healthStatus,
                ContextUsagePercent: contextUsagePercent,
                ContextWindowUsed: contextWindowUsed,
                ContextWindowSize: contextWindowSize,
                RecordedAt: now)];
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
