using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Domain;

public static partial class AgentSessionExtensions
{
    /// <summary>
    /// Target cap for the retained context-usage history. Picked small
    /// so a trend mini-chart still gets a "lifetime" view (issue-245 T-002,
    /// design D5) while the grain state and downstream activity payloads
    /// stay bounded.
    /// </summary>
    public const int ContextUsageHistoryCap = 24;

    /// <summary>
    /// Time-bucket size for context-usage history time-thinning. Within a
    /// bucket only the latest sample is kept (last-wins), so back-to-back
    /// usage updates don't drown out the long-run trend.
    /// </summary>
    public static readonly TimeSpan ContextUsageHistoryBucket = TimeSpan.FromSeconds(30);

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
            DateTime now,
            string? runtime = null,
            string? expectedRuntime = null,
            string? expectedAgentSessionId = null,
            string? expectedRunnerId = null)
        {
            _ = changeDir;
            _ = processPid;
            var oldModel = session.Settings.Model;
            var existingAgentSessionId = session.Status.AgentRuntimeSessionId;
            var existingRuntime = NormalizeRuntime(session.Runtime.Runtime);
            var nextRuntime = NormalizeRuntime(runtime) ?? existingRuntime ?? "opencode";
            if (string.IsNullOrWhiteSpace(nextRuntime))
                throw new InvalidOperationException("AgentSession attach requires a registered runtime.");
            if (expectedRuntime is not null || expectedAgentSessionId is not null || expectedRunnerId is not null)
            {
                var expected = new AgentRuntimeBinding(
                    expectedRunnerId ?? session.Runtime.RunnerId,
                    expectedRuntime,
                    expectedAgentSessionId);
                EnsureExpectedRuntimeBinding(session, expected, session.CurrentRuntimeBinding());
            }
            if (!string.IsNullOrWhiteSpace(session.Runtime.WorkDir)
                && !string.IsNullOrWhiteSpace(workDir)
                && !string.Equals(session.Runtime.WorkDir, workDir, StringComparison.Ordinal))
                throw new InvalidOperationException($"AgentSession {session.Id} is bound to work directory '{session.Runtime.WorkDir}', not '{workDir}'.");
            // attach is a normal operation that reuses the current binding; it is
            // not a binding-replacement entry point. Replacing a bound physical
            // session under the same runtime must go through Reset / recover-missing
            // (idle-only CAS), otherwise a stray attach would silently swap the
            // runtime session the AgentSession is committed to.
            if (!string.IsNullOrWhiteSpace(existingAgentSessionId)
                && string.Equals(existingRuntime, nextRuntime, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(existingAgentSessionId, agentSessionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"AgentSession {session.Id} is already bound to runtime session {existingAgentSessionId}; use Reset to replace the binding.");
            }

            var isNewRuntimeBinding = !string.Equals(existingAgentSessionId, agentSessionId, StringComparison.Ordinal)
                || !string.Equals(existingRuntime, nextRuntime, StringComparison.OrdinalIgnoreCase);

            // A runtime change (e.g. runner restart on a different backend) replaces
            // the binding through the idle-only CAS path so the context window is
            // cleared while cumulative usage is preserved. Same-runtime rebind was
            // already rejected above.
            if (isNewRuntimeBinding && !string.IsNullOrWhiteSpace(existingAgentSessionId)
                && !string.Equals(existingRuntime, nextRuntime, StringComparison.OrdinalIgnoreCase))
            {
                session.Settings = session.Settings with { Model = model ?? session.Settings.Model };
                var replacementEvents = session.RebindRuntimeSession(
                    session.CurrentRuntimeBinding(),
                    new AgentRuntimeBinding(session.Runtime.RunnerId, nextRuntime, agentSessionId),
                    "runtime-change",
                    now: now).ToList();
                if (!string.Equals(oldModel, model ?? oldModel, StringComparison.Ordinal))
                    replacementEvents.Add(new AgentSessionModelChanged(model ?? oldModel));
                return replacementEvents;
            }

            session.Runtime = session.Runtime with
            {
                WorkDir = string.IsNullOrWhiteSpace(session.Runtime.WorkDir) ? workDir : session.Runtime.WorkDir,
                Runtime = nextRuntime,
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
                events.Add(new AgentSessionRuntimeBound(agentSessionId, session.Runtime.Runtime));
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
            session.Status = session.Status with { LastDataAt = now, CurrentTurnEndedAt = null };
            return [];
        }

        public IReadOnlyList<AgentSessionEvent> SetActivity(AgentSessionActivity activity, DateTime now)
        {
            session.Status = session.Status with
            {
                Activity = activity,
                LastDataAt = now,
                CurrentTurnEndedAt = activity == AgentSessionActivity.Idle ? now : session.Status.CurrentTurnEndedAt,
            };
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
            long? contextWindowSize,
            DateTime now,
            long? cachedWriteTokens = null)
        {
            var usage = session.Status.UsageSummary ?? new AgentUsageSummary();
            var newUsed = contextWindowUsed ?? usage.ContextWindowUsed;
            var newSize = contextWindowSize ?? usage.ContextWindowSize;
            session.Status = session.Status with
            {
                UsageSummary = usage with
                {
                    InputTokens = AddNonNegative(usage.InputTokens, inputTokens),
                    OutputTokens = AddNonNegative(usage.OutputTokens, outputTokens),
                    TotalTokens = AddNonNegative(usage.TotalTokens, totalTokens),
                    CachedReadTokens = AddNonNegative(usage.CachedReadTokens, cachedReadTokens),
                    CachedWriteTokens = AddNonNegative(usage.CachedWriteTokens, cachedWriteTokens),
                    ThoughtTokens = AddNonNegative(usage.ThoughtTokens, thoughtTokens),
                    CostAmount = AddNonNegative(usage.CostAmount, costAmount),
                    CostCurrency = costCurrency ?? usage.CostCurrency,
                    ContextWindowUsed = newUsed,
                    ContextWindowSize = newSize
                },
                ContextUsageHistory = AppendUsageHistorySample(session.Status.ContextUsageHistory, newUsed, newSize, now)
            };
            return [new AgentSessionUsageRecorded(session.Status.UsageSummary ?? new AgentUsageSummary())];
        }

        public IReadOnlyList<AgentSessionEvent> RebindRuntimeSession(
            AgentRuntimeBinding expected,
            AgentRuntimeBinding replacement,
            string reason,
            DateTime now)
        {
            if (session.Status.Activity != AgentSessionActivity.Idle)
                throw new InvalidOperationException($"AgentSession {session.Id} is currently {session.Status.Activity}; binding replacement requires idle activity.");
            EnsureExpectedRuntimeBinding(session, expected, session.CurrentRuntimeBinding());
            if (string.IsNullOrWhiteSpace(replacement.RunnerId) || string.IsNullOrWhiteSpace(replacement.RuntimeSessionId))
                throw new InvalidOperationException("Binding replacement requires a runner and runtime session.");
            if (reason is not ("reset" or "runtime-change" or "missing-recovery"))
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unsupported binding replacement reason.");

            session.Runtime = session.Runtime with
            {
                RunnerId = replacement.RunnerId,
                Runtime = NormalizeRuntime(replacement.Runtime),
            };
            var usage = session.Status.UsageSummary ?? new AgentUsageSummary();
            session.Status = session.Status with
            {
                AgentRuntimeSessionId = replacement.RuntimeSessionId,
                BoundAt = now,
                LastDataAt = now,
                UsageSummary = usage with { ContextWindowUsed = null, ContextWindowSize = null },
            };
            return [new AgentSessionRuntimeBound(replacement.RuntimeSessionId, session.Runtime.Runtime)];
        }

        public AgentRuntimeBinding CurrentRuntimeBinding() =>
            new(session.Runtime.RunnerId, NormalizeRuntime(session.Runtime.Runtime), session.Status.AgentRuntimeSessionId);

        private static void EnsureExpectedRuntimeBinding(
            AgentSession actualSession,
            AgentRuntimeBinding expected,
            AgentRuntimeBinding actual)
        {
            if (expected == actual) return;
            throw new StaleRuntimeSessionBindingException(actualSession.Id, expected.RuntimeSessionId, actual.RuntimeSessionId);
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
        /// expose the latest known health snapshot, and the bounded
        /// context-usage history is thinned-appended so a freshly
        /// opened Pulse sees the lifetime trend rather than only the
        /// latest snapshot (issue-245 T-002 / design D5).
        /// </summary>
        public IReadOnlyList<AgentSessionEvent> RecordContextHealthUpdate(
            string healthStatus,
            double? contextUsagePercent,
            long? contextWindowUsed,
            long? contextWindowSize,
            DateTime now)
        {
            session.Status = session.Status with
            {
                LastDataAt = now,
                ContextUsageHistory = AppendUsageHistorySample(
                    session.Status.ContextUsageHistory,
                    contextWindowUsed,
                    contextWindowSize,
                    now)
            };
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

        private static string? NormalizeRuntime(string? runtime) =>
            string.IsNullOrWhiteSpace(runtime) ? null : runtime.Trim();

        private static bool IsRegisteredRuntime(string runtime) =>
            string.Equals(runtime, "opencode", StringComparison.OrdinalIgnoreCase)
            || string.Equals(runtime, "pi", StringComparison.OrdinalIgnoreCase);

        public bool IsRuntimeSessionMissing(Func<string, bool> isRuntimeRegistered)
        {
            ArgumentNullException.ThrowIfNull(isRuntimeRegistered);
            if (string.IsNullOrWhiteSpace(session.Status.AgentRuntimeSessionId))
                return true;

            var runtime = session.Runtime.Runtime;
            if (string.IsNullOrWhiteSpace(runtime))
                return true;

            return !isRuntimeRegistered(runtime);
        }

        /// <summary>
        /// Appends a thinned <see cref="ContextUsageHistoryEntry"/> to
        /// <paramref name="history"/>. Behaviour (issue-245 T-002, design D5):
        /// <list type="bullet">
        ///   <item><description>returns <paramref name="history"/> unchanged
        ///   when <paramref name="contextWindowUsed"/> or
        ///   <paramref name="contextWindowSize"/> cannot produce a finite
        ///   0..100 % (mirrors <see cref="AgentSessionJsonHelper.ContextUsagePercent"/>);</description></item>
        ///   <item><description>coalesces with the last entry when it falls
        ///   inside the same <see cref="ContextUsageHistoryBucket"/> time
        ///   window (last-wins) so back-to-back usage updates don't drown
        ///   the long-run trend;</description></item>
        ///   <item><description>truncates to the most recent
        ///   <see cref="ContextUsageHistoryCap"/> samples so the history
        ///   cannot grow unbounded regardless of session length (bounded
        ///   payload).</description></item>
        /// </list>
        /// </summary>
        private static IReadOnlyList<ContextUsageHistoryEntry>? AppendUsageHistorySample(
            IReadOnlyList<ContextUsageHistoryEntry>? history,
            long? contextWindowUsed,
            long? contextWindowSize,
            DateTime now)
        {
            if (history is null) return null;

            var percent = AgentSessionJsonHelper.ContextUsagePercent(contextWindowUsed, contextWindowSize);
            if (percent is null) return history;

            var entries = new List<ContextUsageHistoryEntry>(history.Count + 1);
            entries.AddRange(history);

            var lastBucket = GetHistoryBucket(entries.Count > 0 ? entries[^1].At : (DateTime?)null);
            var nowBucket = GetHistoryBucket(now);

            if (entries.Count > 0 && lastBucket == nowBucket)
            {
                entries[^1] = new ContextUsageHistoryEntry(now, percent.Value);
            }
            else
            {
                entries.Add(new ContextUsageHistoryEntry(now, percent.Value));
            }

            if (entries.Count > ContextUsageHistoryCap)
            {
                entries.RemoveRange(0, entries.Count - ContextUsageHistoryCap);
            }

            return entries;
        }

        private static long GetHistoryBucket(DateTime? at) =>
            at is null
                ? long.MinValue
                : at.Value.Ticks / ContextUsageHistoryBucket.Ticks;
    }
}
