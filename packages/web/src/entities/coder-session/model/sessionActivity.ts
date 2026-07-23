import type { AgentSessionActivity, SessionStatusKind } from './types'

export function deriveSessionActivity(activity: string | null | undefined): AgentSessionActivity {
  if (activity === 'idle' || activity === 'active') return activity
  return 'unknown'
}

export function deriveSessionStatusKind(activity: string | null | undefined): SessionStatusKind {
  return deriveSessionActivity(activity)
}

export function canFollowupSession(activity: string | null | undefined): boolean {
  return deriveSessionActivity(activity) !== 'unknown'
}

export function canRecoverSession(activity: string | null | undefined): boolean {
  return deriveSessionActivity(activity) === 'idle'
}
