using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Domain;

public static partial class AgentSessionExtensions
{
    /// <summary>
    /// Target cap for the retained context-usage history. Picked small
    /// so a trend mini-chart still gets a "lifetime" view
    /// while the grain state and downstream activity payloads
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

        public IReadOnlyList<AgentSessionEvent> ReconcileMissingBinding(
            AgentRuntimeBinding expected,
            AgentRuntimeBinding replacement,
            DateTime now)
        {
            EnsureExpectedRuntimeBinding(session, expected, session.CurrentRuntimeBinding());
            session.SetActivity(AgentSessionActivity.Idle, now);
            return session.RebindRuntimeSession(expected, replacement, "missing-recovery", now);
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
        /// latest snapshot.
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
        /// Initial-launch transition: opens the session if absent,
        /// records the first <see cref="AgentSessionInputRecord"/> as
        /// accepted, records the first <see cref="AgentTurnRecord"/> as
        /// queued, and links both to the supplied AgentJob id. The
        /// session activity is bumped to active so navigation surfaces
        /// reflect the new work. The transition is idempotent for a
        /// replay carrying the same ids; mismatched ids or
        /// pre-existing immutable source metadata raise a conflict.
        /// </summary>
        /// <param name="attachments">
        /// Ordered child record of attachment descriptors carried by
        /// the accepted input. The transition owns the persistence of
        /// this list alongside the text so the accepted set survives a
        /// grain reload and is queryable via the input surface.
        /// Replays with the same input id must supply an equivalent
        /// (id + name + content-type + size) ordered set; a mismatch
        /// raises a conflict (the launch identity is immutable once
        /// accepted).
        /// </param>
        /// <param name="provenance">
        /// Per-input provenance describing the upstream source the
        /// caller attached (provider kind, workspace id,
        /// conversation id, thread id, member id, message id,
        /// connection id). Persisted on the input record so a
        /// later observer can attribute the accepted input back to
        /// its source. Replays must supply an equivalent provenance
        /// record; a mismatch raises a conflict (the launch identity
        /// is immutable once accepted).
        /// </param>
        /// <param name="startupContext">
        /// Optional bounded external discussion the caller attaches as
        /// first-launch-only background. Persisted verbatim on the
        /// input record (including the truncation attestation) so the
        /// audit is inspectable and a recovery replay observes the
        /// same first-accepted snapshot. <c>prompt</c> stays
        /// task-only — the background is composed into the dispatched
        /// agent input at <c>BuildDispatch</c> time, not at the
        /// SessionInput layer. Null when no startup context was
        /// supplied. Replays with the same input id must supply an
        /// equivalent record (value equality); a mismatch raises a
        /// conflict.
        /// </param>
        public IReadOnlyList<AgentSessionEvent> EnsureInitialLaunch(
            string inputId,
            string turnId,
            string prompt,
            string source,
            string jobId,
            DateTime now,
            IReadOnlyList<AgentSessionInputAttachmentDescriptor>? attachments = null,
            AgentSessionInputProvenance? provenance = null,
            AgentStartupContext? startupContext = null)
        {
            if (string.IsNullOrWhiteSpace(inputId))
                throw new ArgumentException("Input id is required.", nameof(inputId));
            if (string.IsNullOrWhiteSpace(turnId))
                throw new ArgumentException("Turn id is required.", nameof(turnId));
            var normalizedAttachments = NormalizeAttachmentDescriptors(attachments);
            var hasText = !string.IsNullOrWhiteSpace(prompt);
            var hasAttachments = normalizedAttachments is { Count: > 0 };
            if (!hasText && !hasAttachments)
                throw new ArgumentException(
                    "Prompt is required unless at least one attachment is accepted.",
                    nameof(prompt));
            if (string.IsNullOrWhiteSpace(jobId))
                throw new ArgumentException("Job id is required.", nameof(jobId));

            var inputs = (session.Status.Inputs ?? []).ToList();
            var inputIndex = inputs.FindIndex(candidate =>
                string.Equals(candidate.Id, inputId, StringComparison.Ordinal));
            if (inputIndex >= 0)
            {
                var existing = inputs[inputIndex];
                if (!string.Equals(existing.Text, prompt, StringComparison.Ordinal)
                    || !string.Equals(existing.Source, source, StringComparison.Ordinal)
                    || !string.Equals(existing.JobId, jobId, StringComparison.Ordinal)
                    || !AttachmentDescriptorsEquivalent(existing.Attachments, normalizedAttachments)
                    || !Equals(existing.Provenance, provenance)
                    || !Equals(existing.StartupContext, startupContext))
                {
                    throw new InvalidOperationException(
                        $"AgentSession {session.Id} already has input '{inputId}' with different content/source/job/attachments.");
                }
            }
            else
            {
                inputs.Add(new AgentSessionInputRecord(
                    Id: inputId,
                    Sequence: inputs.Count + 1,
                    Text: prompt,
                    Source: source,
                    Acceptance: AgentSessionInputAcceptance.Accepted,
                    RecordedAt: now,
                    JobId: jobId,
                    Attachments: normalizedAttachments,
                    Provenance: provenance,
                    StartupContext: startupContext));
            }

            var turns = (session.Status.Turns ?? []).ToList();
            var turnIndex = turns.FindIndex(candidate =>
                string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
            if (turnIndex >= 0)
            {
                var existing = turns[turnIndex];
                if (!string.Equals(existing.JobId, jobId, StringComparison.Ordinal)
                    || !existing.InputIds.Contains(inputId, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"AgentSession {session.Id} already has turn '{turnId}' with different job/input linkage.");
                }
            }
            else
            {
                turns.Add(new AgentTurnRecord(
                    Id: turnId,
                    Sequence: turns.Count + 1,
                    InputIds: new[] { inputId },
                    Status: AgentTurnStatus.Queued,
                    JobId: jobId,
                    RecordedAt: now,
                    UpdatedAt: now));
            }

            session.Status = session.Status with
            {
                Inputs = inputs,
                Turns = turns,
                Activity = AgentSessionActivity.Active,
                LastDataAt = now,
                CurrentTurnEndedAt = null,
            };

            return [];
        }

        public IReadOnlyList<AgentSessionEvent> RecordFollowupTurn(
            string inputId,
            string turnId,
            string prompt,
            string source,
            DateTime now,
            IReadOnlyList<AgentSessionInputAttachmentDescriptor>? attachments = null,
            AgentSessionInputProvenance? provenance = null)
        {
            if (string.IsNullOrWhiteSpace(inputId))
                throw new ArgumentException("Input id is required.", nameof(inputId));
            if (string.IsNullOrWhiteSpace(turnId))
                throw new ArgumentException("Turn id is required.", nameof(turnId));
            if (string.IsNullOrWhiteSpace(prompt))
                throw new ArgumentException("Prompt is required.", nameof(prompt));
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("Source is required.", nameof(source));

            var normalizedAttachments = NormalizeAttachmentDescriptors(attachments);

            var inputs = (session.Status.Inputs ?? []).ToList();
            var turns = (session.Status.Turns ?? []).ToList();
            var inputIndex = inputs.FindIndex(candidate =>
                string.Equals(candidate.Id, inputId, StringComparison.Ordinal));
            var turnIndex = turns.FindIndex(candidate =>
                string.Equals(candidate.Id, turnId, StringComparison.Ordinal));

            if (inputIndex >= 0)
            {
                var existing = inputs[inputIndex];
                if (!string.Equals(existing.Text, prompt, StringComparison.Ordinal)
                    || !string.Equals(existing.Source, source, StringComparison.Ordinal)
                    || !string.IsNullOrWhiteSpace(existing.JobId)
                    || !AttachmentDescriptorsEquivalent(existing.Attachments, normalizedAttachments)
                    || !Equals(existing.Provenance, provenance))
                {
                    throw new InvalidOperationException(
                        $"AgentSession {session.Id} already has input '{inputId}' with different content/source/job/attachments linkage.");
                }
            }

            if (turnIndex >= 0)
            {
                var existing = turns[turnIndex];
                if (!string.IsNullOrWhiteSpace(existing.JobId)
                    || !existing.InputIds.Contains(inputId, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"AgentSession {session.Id} already has turn '{turnId}' with different job/input linkage.");
                }
            }

            if (inputIndex >= 0 || turnIndex >= 0)
            {
                if (inputIndex < 0 || turnIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"AgentSession {session.Id} has incomplete input/turn linkage for '{inputId}' and '{turnId}'.");
                }
                return [];
            }

            if (turns.Any(candidate => candidate.InputIds.Contains(inputId, StringComparer.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"AgentSession {session.Id} already links input '{inputId}' to another turn.");
            }

            if (session.Status.Activity != AgentSessionActivity.Idle
                || turns.Any(candidate => string.IsNullOrWhiteSpace(candidate.JobId)
                    && (candidate.Status is AgentTurnStatus.Queued
                        or AgentTurnStatus.Executing
                        or AgentTurnStatus.Unknown)))
            {
                throw new InvalidOperationException(
                    $"AgentSession {session.Id} cannot start another turn while work is active.");
            }

            inputs.Add(new AgentSessionInputRecord(
                Id: inputId,
                Sequence: inputs.Count + 1,
                Text: prompt,
                Source: source,
                Acceptance: AgentSessionInputAcceptance.Accepted,
                RecordedAt: now,
                JobId: null,
                Attachments: normalizedAttachments,
                Provenance: provenance));
            turns.Add(new AgentTurnRecord(
                Id: turnId,
                Sequence: turns.Count + 1,
                InputIds: new[] { inputId },
                Status: AgentTurnStatus.Queued,
                JobId: null,
                RecordedAt: now,
                UpdatedAt: now));

            session.Status = session.Status with
            {
                Inputs = inputs,
                Turns = turns,
                Activity = AgentSessionActivity.Active,
                LastDataAt = now,
                CurrentTurnEndedAt = null,
            };

            return [];
        }

        /// <summary>
        /// Mark the initial turn for the given job id as
        /// <see cref="AgentTurnStatus.Executing"/>. No-op if the turn
        /// is already in a non-queued state. Used by the AgentJob
        /// dispatch observer path so the Session's view of the
        /// running turn stays consistent with Job-side progress.
        /// </summary>
        public IReadOnlyList<AgentSessionEvent> MarkInitialTurnExecuting(string jobId, DateTime now)
        {
            var turns = session.Status.Turns ?? [];
            var index = FindTurnIndexByJobId(turns, jobId);
            if (index < 0)
                return [];
            return session.MarkTurnExecuting(turns[index].Id, now);
        }

        public IReadOnlyList<AgentSessionEvent> MarkTurnExecuting(string turnId, DateTime now)
        {
            var turns = (session.Status.Turns ?? []).ToList();
            var index = turns.FindIndex(candidate =>
                string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
            if (index < 0)
                return [];
            if (turns[index].Status is AgentTurnStatus.Executing
                or AgentTurnStatus.Completed
                or AgentTurnStatus.Failed
                or AgentTurnStatus.Cancelled)
            {
                return [];
            }
            var wasUnknown = turns[index].Status == AgentTurnStatus.Unknown;
            turns[index] = turns[index] with
            {
                Status = AgentTurnStatus.Executing,
                UpdatedAt = now,
            };
            session.Status = session.Status with
            {
                Turns = turns,
                Activity = wasUnknown ? AgentSessionActivity.Active : session.Status.Activity,
                LastDataAt = now,
                CurrentTurnEndedAt = wasUnknown ? null : session.Status.CurrentTurnEndedAt,
            };
            return [];
        }

        /// <summary>
        /// Apply a terminal result to the initial turn for the given
        /// job id. The turn moves to Completed, Failed, or Unknown
        /// based on the supplied status. The session remains usable
        /// after a terminal first turn — AgentSession is the
        /// conversation owner, not the work owner.
        /// </summary>
        public IReadOnlyList<AgentSessionEvent> MarkInitialTurnTerminal(
            string jobId,
            AgentTurnStatus status,
            AgentTurnResult? result,
            DateTime now)
        {
            var turns = session.Status.Turns ?? [];
            var index = FindTurnIndexByJobId(turns, jobId);
            if (index < 0)
                return [];
            return session.MarkTurnTerminal(turns[index].Id, status, result, now);
        }

        public IReadOnlyList<AgentSessionEvent> MarkTurnTerminal(
            string turnId,
            AgentTurnStatus status,
            AgentTurnResult? result,
            DateTime now)
        {
            if (status is AgentTurnStatus.Queued or AgentTurnStatus.Executing)
                throw new ArgumentOutOfRangeException(nameof(status), status, "Turn terminal status is required.");

            var turns = (session.Status.Turns ?? []).ToList();
            var index = turns.FindIndex(candidate =>
                string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
            if (index < 0)
                return [];
            if (turns[index].Status is AgentTurnStatus.Completed
                or AgentTurnStatus.Failed
                or AgentTurnStatus.Cancelled
                || turns[index].Status == AgentTurnStatus.Unknown && status == AgentTurnStatus.Unknown)
            {
                return [];
            }

            turns[index] = turns[index] with
            {
                Status = status,
                Result = result,
                UpdatedAt = now,
            };
            var inputIds = turns[index].InputIds;
            var inputs = session.Status.Inputs ?? [];
            var updatedInputs = inputs
                .Select(candidate => inputIds.Contains(candidate.Id, StringComparer.Ordinal)
                    ? candidate with { Acceptance = AgentSessionInputAcceptance.Accepted }
                    : candidate)
                .ToList();
            session.Status = session.Status with
            {
                Turns = turns,
                Inputs = updatedInputs,
                LastDataAt = now,
                Activity = status == AgentTurnStatus.Unknown
                    ? AgentSessionActivity.Unknown
                    : AgentSessionActivity.Idle,
                CurrentTurnEndedAt = now,
            };
            return [];
        }

        public IReadOnlyList<AgentSessionEvent> CancelTurn(string turnId, DateTime now)
        {
            var turns = (session.Status.Turns ?? []).ToList();
            var index = turns.FindIndex(candidate =>
                string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
            if (index < 0)
                return [];
            if (turns[index].Status is AgentTurnStatus.Executing
                or AgentTurnStatus.Completed
                or AgentTurnStatus.Failed
                or AgentTurnStatus.Cancelled
                or AgentTurnStatus.Unknown)
            {
                return [];
            }
            turns[index] = turns[index] with
            {
                Status = AgentTurnStatus.Cancelled,
                UpdatedAt = now,
            };
            session.Status = session.Status with
            {
                Turns = turns,
                LastDataAt = now,
                Activity = AgentSessionActivity.Idle,
                CurrentTurnEndedAt = now,
            };
            return [];
        }

        public AgentTurnCancelResult CancelQueuedTurn(string turnId, DateTime now)
        {
            var control = session.ResolveTurnControl(turnId);
            if (control?.Classification != AgentTurnControlClassification.Queued || control.IsLaunchTurn)
                return new AgentTurnCancelResult(control, false);

            _ = session.CancelTurn(turnId, now);
            return new AgentTurnCancelResult(session.ResolveTurnControl(turnId), true);
        }

        public AgentTurnStopClaimResult ClaimTurnStop(string turnId)
        {
            var control = session.ResolveTurnControl(turnId);
            var pending = session.Status.PendingStop;
            if (control?.Classification == AgentTurnControlClassification.Terminal
                && pending is not null
                && string.Equals(pending.TurnId, turnId, StringComparison.Ordinal))
            {
                return new AgentTurnStopClaimResult(control, true, pending.OperationId);
            }

            if (control?.Classification != AgentTurnControlClassification.Executing)
                return new AgentTurnStopClaimResult(control, false, null);

            if (pending is not null && !string.Equals(pending.TurnId, turnId, StringComparison.Ordinal))
                return new AgentTurnStopClaimResult(control, false, null);

            if (pending is null)
            {
                pending = new AgentSessionStopClaim(turnId, Guid.NewGuid().ToString("N"));
                session.Status = session.Status with { PendingStop = pending };
            }

            return new AgentTurnStopClaimResult(control, true, pending.OperationId);
        }

        public void MarkTurnStopDispatched(string turnId, string operationId)
        {
            var pending = session.Status.PendingStop;
            if (pending is not null
                && string.Equals(pending.TurnId, turnId, StringComparison.Ordinal)
                && string.Equals(pending.OperationId, operationId, StringComparison.Ordinal)
                && !pending.DispatchStarted)
            {
                session.Status = session.Status with
                {
                    PendingStop = pending with { DispatchStarted = true },
                };
            }
        }

        public void AbandonUndispatchedTurnStop(string turnId, string operationId)
        {
            var pending = session.Status.PendingStop;
            if (pending is not null
                && string.Equals(pending.TurnId, turnId, StringComparison.Ordinal)
                && string.Equals(pending.OperationId, operationId, StringComparison.Ordinal)
                && !pending.DispatchStarted)
                session.Status = session.Status with { PendingStop = null };
        }

        public void CompleteTurnStop(string turnId, string operationId)
        {
            if (string.Equals(session.Status.PendingStop?.TurnId, turnId, StringComparison.Ordinal)
                && string.Equals(session.Status.PendingStop?.OperationId, operationId, StringComparison.Ordinal))
                session.Status = session.Status with { PendingStop = null };
        }

        public IReadOnlyList<AgentSessionEvent> AbandonFollowupTurn(string inputId, string turnId, DateTime now)
        {
            var turns = (session.Status.Turns ?? []).ToList();
            var turnIndex = turns.FindIndex(candidate =>
                string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
            if (turnIndex < 0 || turns[turnIndex].JobId is not null
                || turns[turnIndex].Status != AgentTurnStatus.Queued
                || !turns[turnIndex].InputIds.Contains(inputId, StringComparer.Ordinal))
                return [];

            turns.RemoveAt(turnIndex);
            var inputs = (session.Status.Inputs ?? []).Where(candidate =>
                !string.Equals(candidate.Id, inputId, StringComparison.Ordinal)).ToList();
            session.Status = session.Status with
            {
                Turns = turns,
                Inputs = inputs,
                Activity = AgentSessionActivity.Idle,
                LastDataAt = now,
                CurrentTurnEndedAt = now,
            };
            return [];
        }

        /// <summary>
        /// Resolves a Turn by id and classifies it for control-plane
        /// targeting (cancel / stop). Returns <c>null</c> when no Turn
        /// matches; the caller treats that as <c>turn-not-found</c>.
        /// </summary>
        public AgentTurnControlState? ResolveTurnControl(string turnId)
        {
            var turns = session.Status.Turns ?? [];
            var match = turns.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
            if (match is null)
                return null;
            return new AgentTurnControlState(
                TurnId: match.Id,
                Status: match.Status,
                Classification: ClassifyTurn(match.Status),
                IsLaunchTurn: !string.IsNullOrWhiteSpace(match.JobId),
                JobId: match.JobId);
        }

        public AgentTurnControlState? ResolveCurrentTurnControl()
        {
            var turn = (session.Status.Turns ?? [])
                .OrderByDescending(candidate => candidate.Sequence)
                .FirstOrDefault(candidate => ClassifyTurn(candidate.Status) != AgentTurnControlClassification.Terminal);
            return turn is null ? null : session.ResolveTurnControl(turn.Id);
        }

        private static AgentTurnControlClassification ClassifyTurn(AgentTurnStatus status) =>
            status switch
            {
                AgentTurnStatus.Queued => AgentTurnControlClassification.Queued,
                AgentTurnStatus.Executing => AgentTurnControlClassification.Executing,
                AgentTurnStatus.Completed
                    or AgentTurnStatus.Failed
                    or AgentTurnStatus.Cancelled
                    or AgentTurnStatus.Unknown => AgentTurnControlClassification.Terminal,
                _ => AgentTurnControlClassification.Terminal,
            };

        private static int FindTurnIndexByJobId(IReadOnlyList<AgentTurnRecord>? turns, string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId) || turns is null)
                return -1;
            for (var index = 0; index < turns.Count; index++)
            {
                if (string.Equals(turns[index].JobId, jobId, StringComparison.Ordinal))
                    return index;
            }
            return -1;
        }

        /// <summary>
        /// Find the first accepted <see cref="AgentSessionInputRecord"/>
        /// on this session whose stored idempotency key matches the
        /// supplied value exactly. Returns <c>null</c> when no input
        /// matches (the caller should treat the input as new and mint
        /// fresh ids). The lookup does not restrict by acceptance:
        /// any stored key on the agent-owned input list is considered,
        /// so a retry after a terminal turn still resolves to the same
        /// input identity.
        /// </summary>
        public AgentSessionFollowupInputLookup? FindFollowupInputByIdempotencyKey(string idempotencyKey)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return null;
            var inputs = session.Status.Inputs ?? [];
            for (var i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                if (string.Equals(input.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                {
                    var turn = (session.Status.Turns ?? [])
                        .FirstOrDefault(candidate => candidate.InputIds.Contains(input.Id, StringComparer.Ordinal));
                    var lease = (session.Status.PendingFollowups ?? [])
                        .FirstOrDefault(candidate => string.Equals(candidate.TurnId, turn?.Id, StringComparison.Ordinal));
                    return new AgentSessionFollowupInputLookup(input, turn, lease?.OperationId);
                }
            }
            return null;
        }

        /// <summary>
        /// Find the non-terminal (queued or executing) follow-up turn
        /// whose lease carries the supplied operationId, or
        /// <c>null</c> when no such turn exists. Launch turns
        /// (JobId != null) are excluded — those are owned by the
        /// AgentJob and progress via the launch observers.
        /// </summary>
        public AgentTurnRecord? FindFollowupTurnByOperationId(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId))
                return null;
            var lease = (session.Status.PendingFollowups ?? [])
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(candidate.TurnId));
            if (lease is null || string.IsNullOrEmpty(lease.TurnId))
                return null;
            return (session.Status.Turns ?? [])
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, lease.TurnId, StringComparison.Ordinal)
                    && string.IsNullOrEmpty(candidate.JobId)
                    && candidate.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing);
        }

        /// <summary>
        /// Count the number of non-terminal follow-up turns (queued
        /// or executing) on this session. Launch turns are excluded.
        /// </summary>
        public int CountNonTerminalFollowupTurns()
        {
            return (session.Status.Turns ?? [])
                .Count(turn => IsNonTerminalFollowupTurn(session, turn));
        }

        /// <summary>
        /// True if at least one non-terminal follow-up turn exists
        /// on the session. Used by the recovery-idle guard.
        /// </summary>
        public bool HasNonTerminalFollowupTurn() =>
            (session.Status.Turns ?? []).Any(turn => IsNonTerminalFollowupTurn(session, turn));

        /// <summary>
        /// Count the number of accepted follow-up inputs assigned to a
        /// non-terminal follow-up turn. Used by the capacity bound so
        /// a session that fans many inputs into a small number of
        /// turns still hits the cap (e.g. rapid double-sends that
        /// join the same queued turn).
        /// </summary>
        public int CountQueuedFollowupInputs()
        {
            var nonTerminalTurns = (session.Status.Turns ?? [])
                .Where(turn => IsNonTerminalFollowupTurn(session, turn))
                .Select(turn => turn.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (nonTerminalTurns.Count == 0)
                return 0;

            return (session.Status.Inputs ?? [])
                .Count(input => input.JobId is null
                    && input.Acceptance == AgentSessionInputAcceptance.Accepted
                    && (session.Status.Turns ?? [])
                        .Where(turn => nonTerminalTurns.Contains(turn.Id))
                        .Any(turn => turn.InputIds.Contains(input.Id, StringComparer.Ordinal)));
        }

        private static bool IsNonTerminalFollowupTurn(AgentSession currentSession, AgentTurnRecord turn) =>
            string.IsNullOrEmpty(turn.JobId)
            && turn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing
            && (currentSession.Status.Inputs ?? [])
                .Any(input => turn.InputIds.Contains(input.Id, StringComparer.Ordinal)
                    && string.Equals(input.Source, "agent-session-followup", StringComparison.Ordinal));

        /// <summary>
        /// Synchronous follow-up accept transition. Persists a new
        /// <see cref="AgentSessionInputRecord"/> (no JobId), assigns
        /// it to an <see cref="AgentTurnRecord"/> per the turn-
        /// assignment rule (idle/queued-turn joins, executing-turn
        /// creates a new queued turn), and records an accepted
        /// <see cref="AgentSessionFollowupLease"/> carrying the input
        /// and turn ids. The transition is the source of truth for
        /// three-valued follow-up availability: persistence is
        /// synchronous so the caller can rely on the returned
        /// <see cref="AgentSessionFollowupAcceptResult"/> identity
        /// before dispatching to the runner.
        /// </summary>
        /// <param name="text">
        /// Follow-up text. May be empty when the input carries at
        /// least one accepted attachment — the spec's
        /// "non-empty text OR at least one accepted attachment"
        /// constraint is enforced here. Validation already rejects
        /// inputs with neither text nor attachments upstream of the
        /// grain.
        /// </param>
        /// <param name="attachments">
        /// Ordered attachment child record carried by the accepted
        /// input. Stored alongside the text so the accepted set
        /// survives a grain reload and is queryable via the input
        /// surface. Replays with the same idempotency key must
        /// supply an equivalent (id + name + content-type + size)
        /// ordered set; a mismatch raises a conflict.
        /// </param>
        public AgentSessionFollowupAcceptResult AcceptFollowup(
            string inputId,
            string turnId,
            string operationId,
            string text,
            string source,
            string idempotencyKey,
            DateTime now,
            IReadOnlyList<AgentSessionInputAttachmentDescriptor>? attachments = null,
            AgentSessionInputProvenance? provenance = null)
        {
            if (string.IsNullOrWhiteSpace(inputId))
                throw new ArgumentException("Input id is required.", nameof(inputId));
            if (string.IsNullOrWhiteSpace(turnId))
                throw new ArgumentException("Turn id is required.", nameof(turnId));
            if (string.IsNullOrWhiteSpace(operationId))
                throw new ArgumentException("Operation id is required.", nameof(operationId));
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("Source is required.", nameof(source));

            var normalizedAttachments = NormalizeAttachmentDescriptors(attachments);
            var hasText = !string.IsNullOrWhiteSpace(text);
            var hasAttachments = normalizedAttachments is { Count: > 0 };
            if (!hasText && !hasAttachments)
            {
                throw new ArgumentException(
                    "Follow-up input requires non-empty text or at least one accepted attachment.",
                    nameof(text));
            }

            var inputs = (session.Status.Inputs ?? []).ToList();
            var turns = (session.Status.Turns ?? []).ToList();
            var leases = (session.Status.PendingFollowups ?? []).ToList();

            var existing = inputs
                .Where(candidate => candidate.JobId is null
                    && string.Equals(candidate.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                .FirstOrDefault();

            if (existing is not null)
            {
                // Idempotent retry: cannot mutate an already-accepted
                // input's identity (text, source, attachment set must match).
                var expectedText = hasText ? text : string.Empty;
                var existingText = existing.Text ?? string.Empty;
                if (!string.Equals(existingText, expectedText, StringComparison.Ordinal)
                    || !string.Equals(existing.Source, source, StringComparison.Ordinal)
                    || !AttachmentDescriptorsEquivalent(existing.Attachments, normalizedAttachments)
                    || !Equals(existing.Provenance, provenance))
                {
                    throw new InvalidOperationException(
                        $"AgentSession {session.Id} already accepts idempotency key '{idempotencyKey}' with different content.");
                }

                var existingTurn = turns.FirstOrDefault(candidate =>
                    candidate.InputIds.Contains(existing.Id, StringComparer.Ordinal));
                if (existingTurn is null)
                {
                    throw new InvalidOperationException(
                        $"AgentSession {session.Id} accepted input '{existing.Id}' has no assigned turn.");
                }

                var turnStillQueued = existingTurn.Status == AgentTurnStatus.Queued;
                var existingLease = leases.FirstOrDefault(candidate =>
                    string.Equals(candidate.TurnId, existingTurn.Id, StringComparison.Ordinal));
                return new AgentSessionFollowupAcceptResult(
                    InputId: existing.Id,
                    TurnId: existingTurn.Id,
                    OperationId: existingLease?.OperationId ?? operationId,
                    AlreadyAccepted: true,
                    ShouldRedeliver: turnStillQueued,
                    InputAcceptance: existing.Acceptance,
                    TurnStatus: existingTurn.Status);
            }

            var candidateTurn = ChooseFollowupTurnForAssignment(
                turns,
                leases,
                inputs,
                hasAttachments);

            var newInput = new AgentSessionInputRecord(
                Id: inputId,
                Sequence: inputs.Count + 1,
                Text: text ?? string.Empty,
                Source: source,
                Acceptance: AgentSessionInputAcceptance.Accepted,
                RecordedAt: now,
                JobId: null,
                IdempotencyKey: idempotencyKey,
                Attachments: normalizedAttachments,
                Provenance: provenance);

            AgentTurnRecord updatedTurn;
            var createdNewTurn = false;
            if (candidateTurn is null)
            {
                updatedTurn = new AgentTurnRecord(
                    Id: turnId,
                    Sequence: turns.Count + 1,
                    InputIds: [inputId],
                    Status: AgentTurnStatus.Queued,
                    JobId: null,
                    Result: null,
                    RecordedAt: now,
                    UpdatedAt: now);
                createdNewTurn = true;
            }
            else
            {
                var inputIds = candidateTurn.InputIds.ToList();
                inputIds.Add(inputId);
                updatedTurn = candidateTurn with
                {
                    InputIds = inputIds,
                    UpdatedAt = now,
                };
            }

            if (!createdNewTurn)
            {
                var index = turns.FindIndex(candidate => candidate.Id == updatedTurn.Id);
                turns[index] = updatedTurn;
            }
            else
            {
                turns.Add(updatedTurn);
            }
            inputs.Add(newInput);

            var turnOperationId = operationId;
            if (createdNewTurn)
            {
                leases.Add(new AgentSessionFollowupLease(
                    OperationId: operationId,
                    RuntimeSessionId: session.Status.AgentRuntimeSessionId ?? string.Empty,
                    Accepted: true,
                    AcceptedAt: now,
                    StartedAt: now,
                    InputId: inputId,
                    TurnId: updatedTurn.Id));
            }
            else
            {
                turnOperationId = leases.First(candidate =>
                    string.Equals(candidate.TurnId, updatedTurn.Id, StringComparison.Ordinal)).OperationId;
            }

            session.Status = session.Status with
            {
                Inputs = inputs,
                Turns = turns,
                PendingFollowup = null,
                PendingFollowups = leases,
                LastDataAt = now,
                CurrentTurnEndedAt = null,
            };

            return new AgentSessionFollowupAcceptResult(
                InputId: inputId,
                TurnId: updatedTurn.Id,
                OperationId: turnOperationId,
                AlreadyAccepted: false,
                ShouldRedeliver: true,
                InputAcceptance: newInput.Acceptance,
                TurnStatus: updatedTurn.Status,
                Attachments: normalizedAttachments);
        }

        /// <summary>
        /// Resolve the follow-up turn an incoming input should be
        /// assigned to. Returns the existing queued turn whose delivery
        /// payload has not been claimed (joins the new input in submission
        /// order), or <c>null</c> to signal that the caller must create a
        /// new queued turn. A dispatching or executing turn does NOT match.
        /// </summary>
        private static AgentTurnRecord? ChooseFollowupTurnForAssignment(
            IReadOnlyList<AgentTurnRecord> turns,
            IReadOnlyList<AgentSessionFollowupLease> leases,
            IReadOnlyList<AgentSessionInputRecord> inputs,
            bool incomingHasAttachments)
        {
            if (incomingHasAttachments)
                return null;

            for (var i = turns.Count - 1; i >= 0; i--)
            {
                var candidate = turns[i];
                if (!string.IsNullOrEmpty(candidate.JobId))
                    continue;
                if (candidate.Status != AgentTurnStatus.Queued)
                    continue;
                if (leases.Any(lease => string.Equals(lease.TurnId, candidate.Id, StringComparison.Ordinal)
                    && lease.PayloadSealed))
                    continue;
                if (candidate.InputIds.Any(inputId => inputs.Any(input => input.Id == inputId
                    && input.Attachments is { Count: > 0 })))
                    continue;
                return candidate;
            }
            return null;
        }

        /// <summary>
        /// Mark the follow-up turn linked to the supplied
        /// operationId as <see cref="AgentTurnStatus.Executing"/>.
        /// No-op when no matching turn is found, when the turn is
        /// already past executing, or when the lease is missing.
        /// </summary>
        public IReadOnlyList<AgentSessionEvent> MarkFollowupTurnExecuting(
            string operationId,
            DateTime now)
        {
            var leases = session.Status.PendingFollowups ?? [];
            var lease = leases.FirstOrDefault(candidate =>
                string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal));
            if (lease is null || string.IsNullOrEmpty(lease.TurnId))
                return [];

            var turns = (session.Status.Turns ?? []).ToList();
            var index = turns.FindIndex(candidate =>
                string.Equals(candidate.Id, lease.TurnId, StringComparison.Ordinal));
            if (index < 0)
                return [];

            if (turns[index].Status is AgentTurnStatus.Executing
                or AgentTurnStatus.Completed
                or AgentTurnStatus.Failed
                or AgentTurnStatus.Cancelled)
            {
                return [];
            }

            turns[index] = turns[index] with
            {
                Status = AgentTurnStatus.Executing,
                UpdatedAt = now,
            };
            session.Status = session.Status with
            {
                Turns = turns,
                Activity = session.Status.Activity == AgentSessionActivity.Unknown
                    ? AgentSessionActivity.Active
                    : session.Status.Activity,
                LastDataAt = now,
                CurrentTurnEndedAt = null,
            };
            return [];
        }

        /// <summary>
        /// Apply a terminal status to the follow-up turn linked to
        /// the supplied operationId. The turn moves to Completed,
        /// Failed, Unknown, or Cancelled; the matching lease is
        /// cleared as part of the same transition so the per-turn
        /// lease count drops to reflect the turn's terminal state.
        /// </summary>
        public IReadOnlyList<AgentSessionEvent> MarkFollowupTurnTerminal(
            string operationId,
            AgentTurnStatus status,
            AgentTurnResult? result,
            DateTime now)
        {
            var leases = (session.Status.PendingFollowups ?? []).ToList();
            var leaseIndex = leases.FindIndex(candidate =>
                string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal));
            if (leaseIndex < 0)
                return [];

            var lease = leases[leaseIndex];
            var turns = (session.Status.Turns ?? []).ToList();
            var turnIndex = string.IsNullOrEmpty(lease.TurnId)
                ? -1
                : turns.FindIndex(candidate =>
                    string.Equals(candidate.Id, lease.TurnId, StringComparison.Ordinal));

            if (turnIndex >= 0)
            {
                var turn = turns[turnIndex];
                if (turn.Status is not (AgentTurnStatus.Completed
                    or AgentTurnStatus.Failed
                    or AgentTurnStatus.Cancelled))
                {
                    turns[turnIndex] = turn with
                    {
                        Status = status,
                        Result = result,
                        UpdatedAt = now,
                    };
                }
            }

            var remainingLeases = leases
                .Where((candidate, index) => index != leaseIndex)
                .ToArray();
            var remainingFollowupTurns = turns.Count(turn => IsNonTerminalFollowupTurn(session, turn));

            session.Status = session.Status with
            {
                Turns = turnIndex >= 0 ? turns : session.Status.Turns,
                PendingFollowup = remainingLeases.Length == 0 ? null : session.Status.PendingFollowup,
                PendingFollowups = remainingLeases,
                LastDataAt = now,
                Activity = status switch
                {
                    AgentTurnStatus.Unknown => AgentSessionActivity.Unknown,
                    _ => remainingFollowupTurns == 0
                        ? AgentSessionActivity.Idle
                        : session.Status.Activity,
                },
                CurrentTurnEndedAt = status is AgentTurnStatus.Completed
                    or AgentTurnStatus.Failed
                    or AgentTurnStatus.Cancelled
                    or AgentTurnStatus.Unknown
                    ? now
                    : session.Status.CurrentTurnEndedAt,
            };
            return [];
        }

        /// <summary>
        /// Appends a thinned <see cref="ContextUsageHistoryEntry"/> to
        /// <paramref name="history"/>. Behaviour:
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

        /// <summary>
        /// Returns a defensive copy of the supplied attachment
        /// descriptors, preserving order and dropping null / blank-id
        /// entries. The list is stored on the durable input record so
        /// callers may not mutate the original collection after the
        /// transition completes.
        /// </summary>
        private static IReadOnlyList<AgentSessionInputAttachmentDescriptor>? NormalizeAttachmentDescriptors(
            IReadOnlyList<AgentSessionInputAttachmentDescriptor>? descriptors)
        {
            if (descriptors is null || descriptors.Count == 0) return null;
            var copy = new List<AgentSessionInputAttachmentDescriptor>(descriptors.Count);
            foreach (var descriptor in descriptors)
            {
                if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.Id)) continue;
                copy.Add(descriptor);
            }
            return copy.Count == 0 ? null : copy;
        }

        /// <summary>
        /// Idempotency check for the attachment child record on
        /// <see cref="AgentSessionInputRecord"/>. Two descriptor lists
        /// are equivalent when they carry the same ordered ids and
        /// matching name / content-type / size tuples — accepted-at is
        /// intentionally excluded because the wall-clock stamp is not
        /// a property of the immutable launch identity.
        /// </summary>
        private static bool AttachmentDescriptorsEquivalent(
            IReadOnlyList<AgentSessionInputAttachmentDescriptor>? left,
            IReadOnlyList<AgentSessionInputAttachmentDescriptor>? right)
        {
            var leftCount = left?.Count ?? 0;
            var rightCount = right?.Count ?? 0;
            if (leftCount != rightCount) return false;
            if (leftCount == 0) return true;
            for (var index = 0; index < leftCount; index++)
            {
                var a = left![index];
                var b = right![index];
                if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal)) return false;
                if (!string.Equals(a.OriginalFileName, b.OriginalFileName, StringComparison.Ordinal)) return false;
                if (!string.Equals(a.ContentType, b.ContentType, StringComparison.Ordinal)) return false;
                if (a.Size != b.Size) return false;
            }
            return true;
        }
    }
}
