import type { Dispatch, SetStateAction } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { QueryClient } from '@tanstack/react-query'
import {
  dispatchRebaseEvent,
  invalidateApprovalWait,
  type EventName,
  type RebaseConflictState,
} from '../../entities/issue'
import {
  applyInboxHint,
  isHighAttentionKind,
  parseInboxItemPersistedHint,
  shouldSuppressInAppNotice,
} from '../../entities/inbox'
import { EVENT_TYPES, REVERSE_DNS_EVENT_TYPES } from '../../shared/lib/canonical-event-types'
import { decideReverseDnsOutcome } from './model/reverse-dns-outcome'
import {
  notifyApprovalRequestedToast,
  notifyRunLifecycleToast,
} from './model/run-lifecycle-toast'

type QueryClientLike = ReturnType<typeof useQueryClient>
type SetRebaseConflict = Dispatch<SetStateAction<RebaseConflictState | null>>

/**
 * Per-domain context handed to each `DomainHandler`. The two previously-
 * captured closures (`viewedIssueRef.current` and `projectId`) are threaded
 * as explicit fields so every handler is unit-testable without mounting the
 * provider (D5).
 */
export interface HandlerContext {
  eventName: string
  parsed: Record<string, unknown>
  queryClient: QueryClientLike
  setRebaseConflict: SetRebaseConflict
  viewedIssue: number | null
  projectId: string | null
  pathname?: string
}

/**
 * A per-domain handler owns the invalidation set + optional toast + (for
 * stage/issue/workflowRun) the reverse-DNS-outcome gate for the event
 * names routed to it. See design.md#D4.
 */
export type DomainHandler = (ctx: HandlerContext) => void

/**
 * Names whose `agent-activity` query must be invalidated on every receipt.
 * Mirrors the inline branch in the legacy `handleEvent`. Centralised here
 * so the orchestrator's body reduces to a single data-driven check.
 */
export const AGENT_ACTIVITY_EVENT_NAMES: ReadonlySet<string> = new Set<string>([
  'message.delta',
  'reasoning.delta',
  'tool_call.started',
  'tool_call.updated',
  'tool_call.completed',
  'coder_text_chunk',
  'coder_thought_chunk',
  'coder_tool_call',
  'coder_session_started',
  'coder_session_completed',
  'coder_session_failed',
  'coder_session_cancelled',
  'coder_session_status_changed',
  'session.liveness',
  'usage.updated',
])

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
 *
 * Returns `true` when the outcome was handled (caller should skip its
 * default invalidations); `false` when the event is not a reverse-DNS
 * integration outcome (caller runs its default invalidations).
 */
function applyReverseDnsOutcome(
  outcome: ReturnType<typeof decideReverseDnsOutcome>,
  queryClient: QueryClientLike,
  setRebaseConflict: SetRebaseConflict,
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

/**
 * Per-domain handlers. Each owns the invalidation set + optional toast +
 * reverse-DNS-outcome gate for its assigned names. The reverse-DNS-outcome
 * gate is run first; if it returns `handled: true`, the handler returns
 * without firing the default invalidations (mirroring the `break` in the
 * legacy `switch`).
 */
function stageHandler(ctx: HandlerContext): void {
  if (applyReverseDnsOutcome(decideReverseDnsOutcome(ctx.eventName, ctx.parsed), ctx.queryClient, ctx.setRebaseConflict)) {
    return
  }
  ctx.queryClient.invalidateQueries({ queryKey: ['issues'] })
}

function issueHandler(ctx: HandlerContext): void {
  if (applyReverseDnsOutcome(decideReverseDnsOutcome(ctx.eventName, ctx.parsed), ctx.queryClient, ctx.setRebaseConflict)) {
    return
  }
  const { issueId } = ctx.parsed as { issueId: string; projectId: string }
  ctx.queryClient.invalidateQueries({ queryKey: ['issues'] })
  if (issueId) {
    ctx.queryClient.invalidateQueries({ queryKey: ['issues', 'detail', issueId] })
  }
}

function workflowRunHandler(ctx: HandlerContext): void {
  if (applyReverseDnsOutcome(decideReverseDnsOutcome(ctx.eventName, ctx.parsed), ctx.queryClient, ctx.setRebaseConflict)) {
    return
  }
  ctx.queryClient.invalidateQueries({ queryKey: ['agent-status'] })
  ctx.queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
  ctx.queryClient.invalidateQueries({ queryKey: ['issues'] })
  if (ctx.eventName === REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound) {
    ctx.queryClient.invalidateQueries({ queryKey: ['agent-session'] })
    ctx.queryClient.invalidateQueries({ queryKey: ['agent-sessions'] })
  }
  if (ctx.eventName === REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused) {
    const evt = ctx.parsed as { issueId: string }
    notifyRunLifecycleToast(ctx.queryClient, ctx.viewedIssue, evt.issueId, 'pause')
  } else if (ctx.eventName === REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed) {
    const evt = ctx.parsed as { issueId: string }
    notifyRunLifecycleToast(ctx.queryClient, ctx.viewedIssue, evt.issueId, 'error')
  }
}

function approvalHandler(ctx: HandlerContext): void {
  if (ctx.eventName === REVERSE_DNS_EVENT_TYPES.StageApprovalRequested) {
    const evt = ctx.parsed as { issueId: string; projectId: string; issueNumber?: number }
    ctx.queryClient.invalidateQueries({ queryKey: ['issues'] })
    ctx.queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
    notifyApprovalRequestedToast(ctx.queryClient, ctx.viewedIssue, evt)
    return
  }
  // StageApprovalResolved
  ctx.queryClient.invalidateQueries({ queryKey: ['issues'] })
  ctx.queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
  invalidateApprovalWait(ctx.queryClient as QueryClient)
}

function inboxHandler(ctx: HandlerContext): void {
  // The hint is invalidation only — the inbox HTTP API remains the
  // source of truth. We never synthesise an InboxItem from the hint
  // payload here; the shared `['inbox', projectId]` invalidation
  // triggers a refetch which reconciles truth. Project affinity is
  // also enforced server-side (T-002); this is the second line of
  // defence that drops hints for the wrong project without a
  // round-trip.
  const hint = parseInboxItemPersistedHint(ctx.parsed)
  if (!hint) return
  const result = applyInboxHint(hint, ctx.queryClient, { currentProjectId: ctx.projectId })
  // High-attention kinds surface an in-app notice only for the
  // current project (result.applied), with route-based duplicate-
  // notice suppression (T-005 / D7). Suppressed when on the inbox
  // page (items appear live via invalidation) or when viewing the
  // same issue.
  if (
    result.applied
    && isHighAttentionKind(hint.kind)
    && !shouldSuppressInAppNotice(hint, ctx.pathname ?? window.location.pathname, ctx.viewedIssue)
  ) {
    if (hint.kind === 'approval_requested') {
      toast.info(`Issue #${hint.issueNumber} needs approval`)
    } else {
      toast.error(`Issue #${hint.issueNumber} encountered an error`)
    }
  }
}

/**
 * Per-domain dispatch table. Keyed by `EventName`, mapped to one of five
 * per-domain handlers (`stageHandler`, `issueHandler`, `workflowRunHandler`,
 * `approvalHandler`, `inboxHandler`). Each name routes to exactly one
 * handler; the handler owns the invalidation set + toast + reverse-DNS-
 * outcome gate for that name.
 *
 * `Partial` so the table can hold only the routable reverse-DNS names
 * (agent-detail and transcript events are handled before the dispatch in
 * the provider's `handleEvent` and do not need a row here). Adding a row
 * with a name outside `EventName` is rejected at compile time — see the
 * `_AssertRouteSubscribes` guard below.
 */
export const ROUTE: Partial<Record<EventName, DomainHandler>> = {
  [REVERSE_DNS_EVENT_TYPES.StageStarted]: stageHandler,
  [REVERSE_DNS_EVENT_TYPES.StageCompleted]: stageHandler,
  [REVERSE_DNS_EVENT_TYPES.StageFailed]: stageHandler,

  [REVERSE_DNS_EVENT_TYPES.IssueCreated]: issueHandler,
  [REVERSE_DNS_EVENT_TYPES.IssueCancelled]: issueHandler,
  [REVERSE_DNS_EVENT_TYPES.IssueArchived]: issueHandler,
  [REVERSE_DNS_EVENT_TYPES.IssueUnarchived]: issueHandler,
  [REVERSE_DNS_EVENT_TYPES.IssueReopened]: issueHandler,
  [REVERSE_DNS_EVENT_TYPES.IssueWorkStarted]: issueHandler,
  [REVERSE_DNS_EVENT_TYPES.IssueCompleted]: issueHandler,
  [REVERSE_DNS_EVENT_TYPES.IssueLabelsChanged]: issueHandler,
  [REVERSE_DNS_EVENT_TYPES.IssuePriorityChanged]: issueHandler,
  [REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteAdded]: issueHandler,
  [REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteRemoved]: issueHandler,

  [REVERSE_DNS_EVENT_TYPES.WorkflowRunStarted]: workflowRunHandler,
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunResumed]: workflowRunHandler,
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused]: workflowRunHandler,
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunStopped]: workflowRunHandler,
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunCompleted]: workflowRunHandler,
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed]: workflowRunHandler,
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunRetrying]: workflowRunHandler,
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunRerunning]: workflowRunHandler,
  [REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound]: workflowRunHandler,
  [REVERSE_DNS_EVENT_TYPES.AgentSessionUsageRecorded]: workflowRunHandler,
  [REVERSE_DNS_EVENT_TYPES.AgentSessionModelChanged]: workflowRunHandler,

  [REVERSE_DNS_EVENT_TYPES.StageApprovalRequested]: approvalHandler,
  [REVERSE_DNS_EVENT_TYPES.StageApprovalResolved]: approvalHandler,

  [REVERSE_DNS_EVENT_TYPES.InboxItemPersisted]: inboxHandler,
}

/**
 * Compile-time guard: every name in the ROUTE dispatch table must also be
 * in the canonical subscription set (`EVENT_TYPES`). The reverse-DNS names
 * are added to `EventMap` in `entities/issue/@x/events.ts`, and `EVENT_TYPES`
 * is the union of agent-detail, transcript, and reverse-DNS names. If a new
 * route row is added without its name being subscribed, the assignment
 * below will fail to typecheck.
 *
 * The guard is anchored on the `ROUTE` table specifically (not on
 * `EventName` at large) so the invariant "every routable name is
 * subscribed" is checked here, where the routable names live. Moving the
 * ROUTE table away from this guard would silently let an unsubscribed
 * name slip into a row — see design.md#D6.
 *
 * Note: the check uses `[T] extends [never]` rather than `T extends never`
 * because TypeScript collapses `Exclude<..., string[]>` to `never` even
 * when the result is non-empty; wrapping in a tuple prevents the collapse
 * and gives a meaningful conditional check.
 *
 * The runtime assertion `Object.keys(ROUTE).length > 0` keeps the guard
 * from being vacuously satisfied by an accidentally-empty dispatch table
 * — see design.md#D6.
 */
type _AssertRouteSubscribes = [
  Exclude<keyof typeof ROUTE, (typeof EVENT_TYPES)[number]>,
] extends [never]
  ? true
  : false
const _subscriptionCoversRoute: _AssertRouteSubscribes = true
void _subscriptionCoversRoute
if (Object.keys(ROUTE).length === 0) {
  throw new Error('ROUTE dispatch table must be non-empty')
}

/**
 * Reduced `handleEvent` orchestration. The caller (LiveTaskProvider) is
 * responsible for: unwrap envelope, agent-detail dispatch (typed cast),
 * and timeline-event forward. This orchestrator is responsible for: the
 * agent-activity invalidation guard and the per-domain ROUTE dispatch.
 */
export function routeEvent(
  eventName: string,
  parsed: Record<string, unknown>,
  ctx: Omit<HandlerContext, 'eventName' | 'parsed'>,
): void {
  if (AGENT_ACTIVITY_EVENT_NAMES.has(eventName)) {
    ctx.queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
  }
  const handler = ROUTE[eventName as EventName]
  if (handler) {
    handler({
      eventName,
      parsed,
      queryClient: ctx.queryClient,
      setRebaseConflict: ctx.setRebaseConflict,
      viewedIssue: ctx.viewedIssue,
      projectId: ctx.projectId,
      pathname: ctx.pathname,
    })
  }
}
