import type { SessionStatusKind } from './types'

export function deriveSessionStatusKind(activity: string | null | undefined): SessionStatusKind {
  if (activity === 'idle' || activity === 'active') return activity
  return 'unknown'
}

export function canFollowupSession(activity: string | null | undefined): boolean {
  return deriveSessionStatusKind(activity) !== 'unknown'
}
