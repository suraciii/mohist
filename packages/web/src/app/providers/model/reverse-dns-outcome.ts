import type { RebaseConflictState } from '../../../entities/issue'
import { REVERSE_DNS_EVENT_TYPES } from '../../../shared/lib/canonical-event-types'
import { readIssueNumber } from './timeline-live-event'

export type ReverseDnsToastTone = 'success' | 'error'

export interface ReverseDnsToast {
  tone: ReverseDnsToastTone
  message: string
}

/**
 * Subset of `RebaseEvent` the decider produces. Kept narrow (only the two
 * types the outcome handler can dispatch) — the caller branches on
 * `rebaseEvent.type` to forward.
 */
export type ReverseDnsRebaseEvent =
  | { type: 'rebase_completed'; issueNumber: number; rebased: boolean }
  | { type: 'rebase_conflict'; issueNumber: number; conflicts: string[]; status?: string; error?: string }

/**
 * Declarative outcome of routing a reverse-DNS integration event.
 *
 * - `handled: false` means the event carries no reverse-DNS outcome.
 * - `handled: true` means the decider matched one of the outcome arms and
 *   the caller applies the returned rebase/toast side effects.
 */
export type ReverseDnsOutcome =
  | { handled: false }
  | {
    handled: true
    rebaseConflict?: RebaseConflictState | null
    rebaseEvent?: ReverseDnsRebaseEvent
    toast?: ReverseDnsToast
  }

export function readOutcome(parsed: Record<string, unknown>): string | null {
  const outcome = parsed.outcome ?? parsed.result ?? parsed.kind ?? parsed.operation ?? parsed.reason
  return typeof outcome === 'string' ? outcome : null
}

export function isRebasePayload(parsed: Record<string, unknown>): boolean {
  const outcome = readOutcome(parsed)
  return outcome?.includes('rebase') === true || 'rebased' in parsed || 'conflicts' in parsed
}

export function isMergePayload(parsed: Record<string, unknown>): boolean {
  const outcome = readOutcome(parsed)
  return outcome?.includes('merge') === true
}

/**
 * Pure decider: given a reverse-DNS event and its parsed envelope body,
 * returns the declarative outcome. Does not import `@tanstack/react-query`,
 * `sonner`, or React — see LiveTaskProvider.tsx for the effect-application
 * loop that consumes the result.
 */
export function decideReverseDnsOutcome(
  eventName: string,
  parsed: Record<string, unknown>,
): ReverseDnsOutcome {
  const issueNumber = readIssueNumber(parsed)
  if (issueNumber === null) return { handled: false }

  if (eventName === REVERSE_DNS_EVENT_TYPES.IssueCompleted) {
    if (isRebasePayload(parsed)) {
      const rebased = typeof parsed.rebased === 'boolean' ? parsed.rebased : true
      return {
        handled: true,
        rebaseConflict: null,
        rebaseEvent: { type: 'rebase_completed', issueNumber, rebased },
      }
    }
    if (isMergePayload(parsed)) {
      return {
        handled: true,
        toast: { tone: 'success', message: `Issue #${issueNumber} merged successfully` },
      }
    }
  }

  const isFailureEvent = eventName === REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed
    || eventName === REVERSE_DNS_EVENT_TYPES.StageFailed
  if (!isFailureEvent) return { handled: false }

  if (isRebasePayload(parsed)) {
    const conflicts = Array.isArray(parsed.conflicts) ? parsed.conflicts.filter((x): x is string => typeof x === 'string') : []
    const error = typeof parsed.error === 'string' ? parsed.error : undefined
    const state: RebaseConflictState = { issueNumber, conflicts, status: 'failed', error }
    return {
      handled: true,
      rebaseConflict: state,
      rebaseEvent: { type: 'rebase_conflict', issueNumber, conflicts, status: 'failed', error },
      toast: { tone: 'error', message: `Rebase conflict on Issue #${issueNumber}` },
    }
  }
  if (isMergePayload(parsed)) {
    return {
      handled: true,
      toast: { tone: 'error', message: `Merge failed for Issue #${issueNumber}` },
    }
  }
  return { handled: false }
}
