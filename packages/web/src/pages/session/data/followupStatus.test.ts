import { describe, expect, it } from 'vitest'
import { resolveFollowupStatus } from './followupStatus'

describe('resolveFollowupStatus', () => {
  it('uses the authoritative response while the observation is stale', () => {
    const status = resolveFollowupStatus(
      {
        status: 'accepted',
        inputId: 'input-1',
        turnId: 'turn-1',
        inputAcceptance: 'accepted',
        turnStatus: 'executing',
      },
      { id: 'input-1', sequence: 1, source: 'agent-session-followup', acceptance: 'accepted' },
      { id: 'turn-1', sequence: 1, inputIds: ['input-1'], status: 'queued' },
    )

    expect(status.inputAcceptance).toBe('accepted')
    expect(status.turnStatus).toBe('executing')
  })

  it('uses a later observation after the response', () => {
    const status = resolveFollowupStatus(
      {
        status: 'accepted',
        inputId: 'input-1',
        turnId: 'turn-1',
        turnStatus: 'executing',
      },
      undefined,
      { id: 'turn-1', sequence: 1, inputIds: ['input-1'], status: 'completed' },
    )

    expect(status.turnStatus).toBe('completed')
  })
})
