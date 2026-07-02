import { useCallback, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { EventName, LiveTaskState, RebaseConflictState } from '../../entities/issue'
import { dispatchAgentEvent } from '../../entities/agent'
import type { AgentDetailEventMap } from '../../entities/agent'
import { dispatchRebaseEvent } from '../../entities/issue/model/rebase-events'
import { dispatchTimelineEvent } from '../../entities/issue/model/timeline-events'
import { decideReverseDnsOutcome } from './model/reverse-dns-outcome'
import { invalidateApprovalWait } from '../../entities/issue'
import { applyInboxHint, isHighAttentionKind, parseInboxItemPersistedHint, shouldSuppressInAppNotice } from '../../entities/inbox/model/inbox-effects'
import { useProject } from '../../entities/project'
import { LiveTaskContext } from '../../entities/issue'
import { useEventsConnection } from '../../shared/api/events-hub'
import { EVENT_TYPES, REVERSE_DNS_EVENT_TYPES } from '../../shared/lib/canonical-event-types'
import {
  isAgentDetailEvent,
  routeTranscriptEventName,
  unwrapEnvelope,
  unwrapTranscriptEnvelope,
} from './model/event-envelope'
import { buildTimelineLiveEvent } from './model/timeline-live-event'
import {
  notifyApprovalRequestedToast,
  notifyRunLifecycleToast,
} from './model/run-lifecycle-toast'
import { useRunnerDropNotice } from './use-runner-drop-notice'
import { getCurrentIssueNumber, useViewedIssueRef } from './use-viewed-issue'

/**
 * Compile-time guard: every name the switch can route (i.e. every key of
 * `EventMap`) must be in the canonical subscription set. The reverse-DNS
 * names are added to `EventMap` in `entities/issue/@x/events.ts` and the
 * `EVENT_TYPES` constant is the union of agent-detail, transcript, and
 * reverse-DNS names. If a new switch arm is added without adding its event
 * type to `EVENT_TYPES`, the assignment below will fail to typecheck.
 *
 * Note: the check uses `[T] extends [never]` rather than `T extends never`
 * because TypeScript collapses `Exclude<..., string[]>` to `never` even
 * when the result is non-empty; wrapping in a tuple prevents the collapse
 * and gives a meaningful conditional check.
 */
type _AssertEventNameSubscribed = [Exclude<EventName, (typeof EVENT_TYPES)[number]>] extends [never]
  ? true
  : false
const _subscriptionCoversSwitch: _AssertEventNameSubscribed = true
void _subscriptionCoversSwitch

export const __testing__ = { unwrapEnvelope, unwrapTranscriptEnvelope, routeTranscriptEventName, buildTimelineLiveEvent, parseInboxItemPersistedHint, getCurrentIssueNumber }


/**
 * Apply the declarative result of `decideReverseDnsOutcome` to its four
 * real-world sinks. The four sinks are mutually independent (none awaits
 * another, none reads another's result in-call), so a single canonical
 * order preserves observable behavior across every arm — the legacy
 * per-arm order was not uniform (rebase arms invalidated last, merge
 * arms first) and is intentionally not reproduced per-arm.
 *
 * Canonical order:
 *   1. invalidations   (`queryClient.invalidateQueries` for each key)
 *   2. setRebaseConflict (null clears; undefined leaves the state unchanged)
 *   3. dispatchRebaseEvent
 *   4. toast
 */
function applyReverseDnsOutcome(
  outcome: ReturnType<typeof decideReverseDnsOutcome>,
  queryClient: ReturnType<typeof useQueryClient>,
  setRebaseConflict: React.Dispatch<React.SetStateAction<RebaseConflictState | null>>,
): boolean {
  if (!outcome.handled) return false
  for (const queryKey of outcome.invalidations) {
    queryClient.invalidateQueries({ queryKey: queryKey as unknown[] })
  }
  if (outcome.rebaseConflict !== undefined) {
    setRebaseConflict(outcome.rebaseConflict)
  }
  if (outcome.rebaseEvent) {
    dispatchRebaseEvent(outcome.rebaseEvent)
  }
  if (outcome.toast) {
    if (outcome.toast.tone === 'success') {
      toast.success(outcome.toast.message)
    } else {
      toast.error(outcome.toast.message)
    }
  }
  return true
}

function useLiveEvents(projectId: string | null): LiveTaskState {
  const queryClient = useQueryClient()
  const [activeTaskId] = useState<string | null>(null)
  const [activeTaskElapsedMs] = useState<number | null>(null)
  const [rebaseConflict, setRebaseConflict] = useState<RebaseConflictState | null>(null)
  const viewedIssueRef = useViewedIssueRef()
  // Subscribe to SignalR connection transitions so transport notices are
  // routed to the toast host / Activity surface instead of any inline issue
  // content. The hook itself owns publishing.
  useRunnerDropNotice()

  const handleEvent = useCallback(
    (eventName: string, rawData: unknown, options?: { dispatchAgentDetail?: boolean }) => {
      try {
        const parsed = unwrapEnvelope(rawData)

        if (options?.dispatchAgentDetail !== false && isAgentDetailEvent(eventName)) {
          dispatchAgentEvent(eventName, parsed as AgentDetailEventMap[typeof eventName])
        }

        if (
          eventName === 'message.delta' ||
          eventName === 'reasoning.delta' ||
          eventName === 'tool_call.started' ||
          eventName === 'tool_call.updated' ||
          eventName === 'tool_call.completed' ||
          eventName === 'coder_text_chunk' ||
          eventName === 'coder_thought_chunk' ||
          eventName === 'coder_tool_call' ||
          eventName === 'coder_session_started' ||
          eventName === 'coder_session_completed' ||
          eventName === 'coder_session_failed' ||
          eventName === 'coder_session_cancelled' ||
          eventName === 'coder_session_status_changed' ||
          eventName === 'session.liveness' ||
          eventName === 'usage.updated'
        ) {
          queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
        }

        switch (eventName as EventName) {
          case REVERSE_DNS_EVENT_TYPES.StageStarted:
          case REVERSE_DNS_EVENT_TYPES.StageCompleted:
          case REVERSE_DNS_EVENT_TYPES.StageFailed: {
            if (applyReverseDnsOutcome(decideReverseDnsOutcome(eventName, parsed), queryClient, setRebaseConflict)) {
              break
            }
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            break
          }
          case REVERSE_DNS_EVENT_TYPES.IssueCreated:
          case REVERSE_DNS_EVENT_TYPES.IssueClosed:
          case REVERSE_DNS_EVENT_TYPES.IssueArchived:
          case REVERSE_DNS_EVENT_TYPES.IssueUnarchived:
          case REVERSE_DNS_EVENT_TYPES.IssueReopened:
          case REVERSE_DNS_EVENT_TYPES.IssueWorkStarted:
          case REVERSE_DNS_EVENT_TYPES.IssueWorkCompleted:
          case REVERSE_DNS_EVENT_TYPES.IssueLabelsChanged:
          case REVERSE_DNS_EVENT_TYPES.IssuePriorityChanged:
          case REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteAdded:
          case REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteRemoved: {
            if (applyReverseDnsOutcome(decideReverseDnsOutcome(eventName, parsed), queryClient, setRebaseConflict)) {
              break
            }
            const { issueId } = parsed as { issueId: string; projectId: string }
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            if (issueId) {
              queryClient.invalidateQueries({ queryKey: ['issues', 'detail', issueId] })
            }
            break
          }
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunStarted:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunResumed:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunStopped:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunCompleted:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunRetrying:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunRerunning:
          case REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound:
          case REVERSE_DNS_EVENT_TYPES.AgentSessionUsageRecorded:
          case REVERSE_DNS_EVENT_TYPES.AgentSessionModelChanged: {
            if (applyReverseDnsOutcome(decideReverseDnsOutcome(eventName, parsed), queryClient, setRebaseConflict)) {
              break
            }
            queryClient.invalidateQueries({ queryKey: ['agent-status'] })
            queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            if (eventName === REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused) {
              const evt = parsed as { issueId: string }
              notifyRunLifecycleToast(queryClient, viewedIssueRef.current, evt.issueId, 'pause')
            } else if (eventName === REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed) {
              const evt = parsed as { issueId: string }
              notifyRunLifecycleToast(queryClient, viewedIssueRef.current, evt.issueId, 'error')
            }
            break
          }
          case REVERSE_DNS_EVENT_TYPES.StageApprovalRequested: {
            const evt = parsed as { issueId: string; projectId: string; issueNumber?: number }
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
            notifyApprovalRequestedToast(queryClient, viewedIssueRef.current, evt)
            break
          }
          case REVERSE_DNS_EVENT_TYPES.StageApprovalResolved: {
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
            invalidateApprovalWait(queryClient)
            break
          }
          case REVERSE_DNS_EVENT_TYPES.InboxItemPersisted: {
            // The hint is invalidation only — the inbox HTTP API remains the
            // source of truth. We never synthesise an InboxItem from the hint
            // payload here; the shared `['inbox', projectId]` invalidation
            // triggers a refetch which reconciles truth. Project affinity is
            // also enforced server-side (T-002); this is the second line of
            // defence that drops hints for the wrong project without a
            // round-trip.
            const hint = parseInboxItemPersistedHint(parsed)
            if (hint) {
              const result = applyInboxHint(hint, queryClient, { currentProjectId: projectId })
              // High-attention kinds surface an in-app notice only for the
              // current project (result.applied), with route-based duplicate-
              // notice suppression (T-005 / D7). Suppressed when on the inbox
              // page (items appear live via invalidation) or when viewing the
              // same issue.
              if (
                result.applied
                && isHighAttentionKind(hint.kind)
                && !shouldSuppressInAppNotice(hint, window.location.pathname, viewedIssueRef.current)
              ) {
                if (hint.kind === 'approval_requested') {
                  toast.info(`Issue #${hint.issueNumber} needs approval`)
                } else {
                  toast.error(`Issue #${hint.issueNumber} encountered an error`)
                }
              }
            }
            break
          }
        }

        const timelineEvent = buildTimelineLiveEvent(eventName, rawData, parsed)
        dispatchTimelineEvent(timelineEvent)
      } catch {
        // ignore malformed events
      }
    },
    [queryClient, projectId],
  )

  const handleTranscriptEvent = useCallback(
    (rawData: unknown) => {
      const transcript = unwrapTranscriptEnvelope(rawData)
      if (!transcript) return
      const routedName = routeTranscriptEventName(transcript.eventName)
      if (isAgentDetailEvent(routedName)) {
        dispatchAgentEvent(
          routedName,
          transcript.detail as AgentDetailEventMap[typeof routedName],
        )
      }
      handleEvent(routedName, transcript.payload, { dispatchAgentDetail: false })
    },
    [handleEvent],
  )

  useEventsConnection(projectId, handleEvent, handleTranscriptEvent)

  return { activeTaskId, activeTaskElapsedMs, rebaseConflict }
}

export function LiveTaskProvider({ children }: { children: React.ReactNode }) {
  const { projectId } = useProject()
  const state = useLiveEvents(projectId)
  return (
    <LiveTaskContext.Provider value={state}>
      {children}
    </LiveTaskContext.Provider>
  )
}
