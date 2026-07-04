import { describe, expect, it, vi } from 'vitest'
import { getStopConsequenceCopy, invokeAction } from './runtime-action-handlers'
import type { RuntimeDecision } from './model/derive-runtime-decision'
import type { RuntimeDecisionSurfaceMutations } from './ui/RuntimeDecisionSurface'

function mutation<TMutation extends { mutate: unknown; isPending: boolean; error: Error | null } = RuntimeDecisionSurfaceMutations['startMutation']>(overrides: Partial<TMutation> = {}): TMutation {
  return {
    mutate: vi.fn() as TMutation['mutate'],
    isPending: false,
    error: null,
    ...overrides,
  } as TMutation
}

function mutations(overrides: Partial<RuntimeDecisionSurfaceMutations> = {}): RuntimeDecisionSurfaceMutations {
  return {
    approveMutation: mutation(),
    sendBackMutation: mutation<RuntimeDecisionSurfaceMutations['sendBackMutation']>(),
    retryMutation: mutation(),
    resumeMutation: mutation(),
    rerunMutation: mutation(),
    forceStopMutation: mutation(),
    stopMutation: mutation(),
    startMutation: mutation(),
    ...overrides,
  }
}

function decision(overrides: Partial<RuntimeDecision> = {}): RuntimeDecision {
  return {
    summary: 'running',
    headline: 'Workflow running',
    rationale: 'The workflow is currently executing.',
    currentTask: null,
    nextAction: 'No user action required right now.',
    primary: null,
    actions: [],
    stopRecoverable: null,
    waitReason: null,
    driftNote: null,
    blockedReason: null,
    approvalStage: null,
    ...overrides,
  }
}

describe('getStopConsequenceCopy', () => {
  it('returns the recoverable copy when stopRecoverable is true', () => {
    expect(getStopConsequenceCopy(true)).toEqual({
      title: 'Stop (recoverable)',
      body: 'Stop will preserve progress so this workflow can be resumed later.',
    })
  })

  it('returns the irreversible copy when stopRecoverable is false', () => {
    expect(getStopConsequenceCopy(false)).toEqual({
      title: 'Stop (irreversible)',
      body: 'Stop is irreversible for this workflow run; progress cannot be resumed.',
    })
  })

  it('returns the irreversible copy when stopRecoverable is null', () => {
    expect(getStopConsequenceCopy(null)).toEqual({
      title: 'Stop (irreversible)',
      body: 'Stop is irreversible for this workflow run; progress cannot be resumed.',
    })
  })
})

describe('invokeAction', () => {
  it('routes approve to approveMutation.mutate with no payload', () => {
    const m = mutations()
    invokeAction('approve', { decision: decision(), mutations: m })
    expect(m.approveMutation.mutate).toHaveBeenCalledTimes(1)
    expect(m.approveMutation.mutate).toHaveBeenCalledWith()
  })

  it('routes retry, resume, rerun, and start each to their own mutation.mutate with no payload', () => {
    for (const [kind, key] of [
      ['retry', 'retryMutation'],
      ['resume', 'resumeMutation'],
      ['rerun', 'rerunMutation'],
      ['start', 'startMutation'],
    ] as const) {
      const m = mutations()
      invokeAction(kind, { decision: decision(), mutations: m })
      expect(m[key].mutate).toHaveBeenCalledTimes(1)
      expect(m[key].mutate).toHaveBeenCalledWith()
    }
  })

  it('routes stop to forceStopMutation when stopRecoverable is true', () => {
    const m = mutations()
    invokeAction('stop', { decision: decision({ stopRecoverable: true }), mutations: m })
    expect(m.forceStopMutation.mutate).toHaveBeenCalledTimes(1)
    expect(m.forceStopMutation.mutate).toHaveBeenCalledWith()
    expect(m.stopMutation.mutate).not.toHaveBeenCalled()
  })

  it('routes stop to stopMutation when stopRecoverable is false', () => {
    const m = mutations()
    invokeAction('stop', { decision: decision({ stopRecoverable: false }), mutations: m })
    expect(m.stopMutation.mutate).toHaveBeenCalledTimes(1)
    expect(m.stopMutation.mutate).toHaveBeenCalledWith()
    expect(m.forceStopMutation.mutate).not.toHaveBeenCalled()
  })

  it('routes stop to stopMutation when stopRecoverable is null', () => {
    const m = mutations()
    invokeAction('stop', { decision: decision({ stopRecoverable: null }), mutations: m })
    expect(m.stopMutation.mutate).toHaveBeenCalledTimes(1)
    expect(m.forceStopMutation.mutate).not.toHaveBeenCalled()
  })

  it('routes send-back to sendBackMutation with { stage, body } using decision.approvalStage', () => {
    const m = mutations()
    invokeAction('send-back', {
      decision: decision({ approvalStage: 'check' }),
      mutations: m,
      sendBackBody: 'Please address the verification failure.',
    })
    expect(m.sendBackMutation.mutate).toHaveBeenCalledTimes(1)
    expect(m.sendBackMutation.mutate).toHaveBeenCalledWith({
      stage: 'check',
      body: 'Please address the verification failure.',
    })
  })

  it('trims the send-back body before invoking', () => {
    const m = mutations()
    invokeAction('send-back', {
      decision: decision({ approvalStage: 'check' }),
      mutations: m,
      sendBackBody: '   Please address the verification failure.   \n',
    })
    expect(m.sendBackMutation.mutate).toHaveBeenCalledWith({
      stage: 'check',
      body: 'Please address the verification failure.',
    })
  })

  it('forwards onSendBackSuccess to the sendBackMutation mutate options', () => {
    const m = mutations()
    const onSendBackSuccess = vi.fn()
    invokeAction('send-back', {
      decision: decision({ approvalStage: 'check' }),
      mutations: m,
      sendBackBody: 'feedback',
      callbacks: { onSendBackSuccess },
    })
    expect(m.sendBackMutation.mutate).toHaveBeenCalledWith(
      { stage: 'check', body: 'feedback' },
      { onSuccess: onSendBackSuccess },
    )
  })

  it('does not call sendBackMutation when approvalStage is missing', () => {
    const m = mutations()
    invokeAction('send-back', {
      decision: decision({ approvalStage: null }),
      mutations: m,
      sendBackBody: 'feedback',
    })
    expect(m.sendBackMutation.mutate).not.toHaveBeenCalled()
  })

  it('does not call sendBackMutation when body is empty', () => {
    const m = mutations()
    invokeAction('send-back', {
      decision: decision({ approvalStage: 'check' }),
      mutations: m,
      sendBackBody: '   ',
    })
    expect(m.sendBackMutation.mutate).not.toHaveBeenCalled()
  })

  it('does not call sendBackMutation when body is undefined', () => {
    const m = mutations()
    invokeAction('send-back', {
      decision: decision({ approvalStage: 'check' }),
      mutations: m,
    })
    expect(m.sendBackMutation.mutate).not.toHaveBeenCalled()
  })

  it('is a no-op for inspect kind (no mutation invoked)', () => {
    const m = mutations()
    invokeAction('inspect', { decision: decision(), mutations: m })
    for (const key of [
      'approveMutation',
      'sendBackMutation',
      'retryMutation',
      'resumeMutation',
      'rerunMutation',
      'forceStopMutation',
      'stopMutation',
      'startMutation',
    ] as const) {
      expect(m[key].mutate).not.toHaveBeenCalled()
    }
  })
})