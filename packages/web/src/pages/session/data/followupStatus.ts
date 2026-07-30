import type { AgentTurnObservation, FollowupStatus, SessionFollowupResult, SessionInputObservation } from '../../../entities/coder-session'

const turnStatusRank: Record<string, number> = {
  queued: 0,
  executing: 1,
  completed: 2,
  failed: 2,
  cancelled: 2,
  unknown: 2,
}

export function resolveFollowupStatus(
  result: SessionFollowupResult,
  input?: SessionInputObservation,
  turn?: AgentTurnObservation,
): FollowupStatus {
  const responseTurnStatus = result.turnStatus ?? null
  const observedTurnStatus = turn?.status ?? null
  const turnStatus = observedTurnStatus !== null
    && (turnStatusRank[observedTurnStatus] ?? -1) >= (turnStatusRank[responseTurnStatus ?? ''] ?? -1)
    ? observedTurnStatus
    : responseTurnStatus

  return {
    outcome: result.status,
    inputId: result.inputId,
    turnId: result.turnId,
    inputAcceptance: result.inputAcceptance ?? input?.acceptance ?? (result.status === 'accepted' ? 'accepted' : null),
    turnStatus: turnStatus ?? (result.status === 'accepted' ? 'queued' : null),
  }
}
