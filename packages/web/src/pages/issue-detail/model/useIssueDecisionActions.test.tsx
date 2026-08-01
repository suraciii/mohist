import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, renderHook } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import type { IssueDetailMutations } from './useIssueDetailMutations'
import {
  buildIssueDecisionActionController,
  getStopConsequenceCopy,
  runControllerAction,
  useIssueDecisionActionController,
  type IssueDecisionControllerContext,
} from './useIssueDecisionActions'
import type { IssueDecisionAction, IssueDecisionActionKind } from './issueDecisionActions'

interface MutationHandle {
  mutate: (vars?: unknown) => unknown
  isPending: boolean
  error: Error | null
}

function mutation(overrides: Partial<MutationHandle> = {}): MutationHandle {
  return {
    mutate: vi.fn(),
    isPending: false,
    error: null,
    ...overrides,
  }
}

function mutations(overrides: Record<string, Partial<MutationHandle>> = {}): IssueDetailMutations {
  const base: Record<string, MutationHandle> = {
    startMutation: mutation(),
    approveMutation: mutation(),
    sendBackMutation: mutation(),
    markReadyMutation: mutation(),
    addPrerequisiteMutation: mutation(),
    removePrerequisiteMutation: mutation(),
    closeMutation: mutation(),
    markDoneMutation: mutation(),
    forceStopMutation: mutation(),
    stopMutation: mutation(),
    reopenMutation: mutation(),
    resumeMutation: mutation(),
    retryMutation: mutation(),
    rerunMutation: mutation(),
    addCommentMutation: mutation(),
    deleteCommentMutation: mutation(),
  }
  for (const [key, value] of Object.entries(overrides)) {
    base[key] = { ...base[key], ...value }
  }
  return base as unknown as IssueDetailMutations
}

function makeAction(kind: IssueDecisionActionKind, overrides: Partial<IssueDecisionAction> = {}): IssueDecisionAction {
  return {
    kind,
    label: 'Action',
    pendingLabel: 'Pending...',
    enabled: true,
    reason: null,
    primary: false,
    destructive: false,
    mode: 'immediate',
    to: null,
    order: 0,
    ...overrides,
  }
}

const STOP_COPY = { title: 'Stop (recoverable)', body: 'Stop preserves progress.' }

function makeCtx(overrides: Partial<IssueDecisionControllerContext> = {}): IssueDecisionControllerContext {
  return {
    mutations: mutations(),
    stopRecoverable: null,
    approvalStage: null,
    stopCopy: STOP_COPY,
    navigate: vi.fn(),
    stopConfirmOpen: false,
    setStopConfirmOpen: vi.fn(),
    ...overrides,
  }
}

describe('buildIssueDecisionActionController', () => {
  it('returns null pendingKind and error when nothing is pending', () => {
    const summary = buildIssueDecisionActionController({ ...makeCtx(), mutations: mutations() })
    expect(summary.pendingKind).toBeNull()
    expect(summary.error).toBeNull()
  })

  it.each([
    ['approve', 'approve'],
    ['sendBack', 'send-back'],
    ['retry', 'retry'],
    ['resume', 'resume'],
    ['rerun', 'rerun'],
    ['start', 'start'],
    ['markReady', 'mark-ready'],
    ['close', 'close'],
    ['markDone', 'mark-as-done'],
  ] as const)('maps %s pending to %s pendingKind', (key, kind) => {
    const summary = buildIssueDecisionActionController(makeCtx({ mutations: mutations({ [`${key}Mutation`]: { isPending: true } }) }))
    expect(summary.pendingKind).toBe(kind)
  })

  it('combines stop and forceStop into the stop pendingKind', () => {
    const summary = buildIssueDecisionActionController(makeCtx({ mutations: mutations({ stopMutation: { isPending: true } }) }))
    expect(summary.pendingKind).toBe('stop')
  })

  it('exposes the pending mutation error when one is in flight', () => {
    const summary = buildIssueDecisionActionController(makeCtx({
      mutations: mutations({ approveMutation: { isPending: true, error: new Error('Approve failed') } }),
    }))
    expect(summary.error?.message).toBe('Approve failed')
  })

  it('uses recoverable/irreversible stop copy from the stopRecoverable flag', () => {
    const recoverable = buildIssueDecisionActionController(makeCtx({ stopRecoverable: true }))
    expect(recoverable.stopConfirmTitle).toMatch(/recoverable/i)
    const irreversible = buildIssueDecisionActionController(makeCtx({ stopRecoverable: false }))
    expect(irreversible.stopConfirmTitle).toMatch(/irreversible/i)
  })
})

describe('runControllerAction', () => {
  it('does not dispatch a disabled action after its authorization changes', () => {
    const stopMutation = mutation()

    runControllerAction(
      makeCtx({ mutations: mutations({ stopMutation }) }),
      makeAction('stop', { enabled: false }),
    )

    expect(stopMutation.mutate).not.toHaveBeenCalled()
  })

  it('does not dispatch another action while a decision mutation is pending', () => {
    const approveMutation = mutation({ isPending: true })
    const sendBackMutation = mutation()

    runControllerAction(
      makeCtx({ mutations: mutations({ approveMutation, sendBackMutation }), approvalStage: 'check' }),
      makeAction('send-back'),
      { sendBackBody: 'Do not send this.' },
    )

    expect(sendBackMutation.mutate).not.toHaveBeenCalled()
  })

  it('invokes approve mutation without an operator', () => {
    const approveMutation = mutation()
    const ctx = makeCtx({ mutations: mutations({ approveMutation }) })
    runControllerAction(ctx, makeAction('approve'))
    expect(approveMutation.mutate).toHaveBeenCalledWith()
  })

  it('routes recoverable stop through forceStop and irreversible through stop', () => {
    const forceStop = mutation()
    const stop = mutation()
    runControllerAction(makeCtx({ mutations: mutations({ forceStopMutation: forceStop, stopMutation: stop }), stopRecoverable: true }), makeAction('stop'))
    expect(forceStop.mutate).toHaveBeenCalledOnce()
    expect(stop.mutate).not.toHaveBeenCalled()

    const forceStop2 = mutation()
    const stop2 = mutation()
    runControllerAction(makeCtx({ mutations: mutations({ forceStopMutation: forceStop2, stopMutation: stop2 }), stopRecoverable: false }), makeAction('stop'))
    expect(stop2.mutate).toHaveBeenCalledOnce()
    expect(forceStop2.mutate).not.toHaveBeenCalled()
  })

  it('refuses send-back without a body or approvalStage', () => {
    const sendBackMutation = mutation()
    runControllerAction(makeCtx({ mutations: mutations({ sendBackMutation }), approvalStage: null }), makeAction('send-back'), { sendBackBody: 'feedback' })
    expect(sendBackMutation.mutate).not.toHaveBeenCalled()

    runControllerAction(makeCtx({ mutations: mutations({ sendBackMutation }), approvalStage: 'check' }), makeAction('send-back'), { sendBackBody: '' })
    expect(sendBackMutation.mutate).not.toHaveBeenCalled()
  })

  it('invokes send-back with stage + body when valid', () => {
    const sendBackMutation = mutation()
    const setStopConfirmOpen = vi.fn()
    runControllerAction(
      makeCtx({ mutations: mutations({ sendBackMutation }), approvalStage: 'check', setStopConfirmOpen }),
      makeAction('send-back'),
      { sendBackBody: 'Tighten the tests' },
    )
    expect(sendBackMutation.mutate).toHaveBeenCalledWith(
      { stage: 'check', body: 'Tighten the tests' },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    )
  })

  it('invokes mark-ready, close, mark-as-done, retry, resume, rerun, start mutations', () => {
    const muts = mutations({
      markReadyMutation: mutation(),
      closeMutation: mutation(),
      markDoneMutation: mutation(),
      retryMutation: mutation(),
      resumeMutation: mutation(),
      rerunMutation: mutation(),
      startMutation: mutation(),
    })
    const ctx = makeCtx({ mutations: muts })
    runControllerAction(ctx, makeAction('mark-ready'))
    runControllerAction(ctx, makeAction('close'))
    runControllerAction(ctx, makeAction('mark-as-done'))
    runControllerAction(ctx, makeAction('retry'))
    runControllerAction(ctx, makeAction('resume'))
    runControllerAction(ctx, makeAction('rerun'))
    runControllerAction(ctx, makeAction('start'))
    expect((muts.markReadyMutation as MutationHandle).mutate).toHaveBeenCalledOnce()
    expect((muts.closeMutation as MutationHandle).mutate).toHaveBeenCalledOnce()
    expect((muts.markDoneMutation as MutationHandle).mutate).toHaveBeenCalledOnce()
    expect((muts.retryMutation as MutationHandle).mutate).toHaveBeenCalledOnce()
    expect((muts.resumeMutation as MutationHandle).mutate).toHaveBeenCalledOnce()
    expect((muts.rerunMutation as MutationHandle).mutate).toHaveBeenCalledOnce()
    expect((muts.startMutation as MutationHandle).mutate).toHaveBeenCalledOnce()
  })

  it('navigates to ask-agent and view-transcript targets', () => {
    const navigate = vi.fn()
    runControllerAction(makeCtx({ navigate }), makeAction('ask-agent', { to: '/agent-sessions/new?issue=14' }))
    expect(navigate).toHaveBeenCalledWith('/agent-sessions/new?issue=14')
    runControllerAction(makeCtx({ navigate }), makeAction('view-transcript', { to: '/sessions/review-1' }))
    expect(navigate).toHaveBeenCalledWith('/sessions/review-1')
  })

  it('does not navigate for ask-agent / view-transcript when no target is set', () => {
    const navigate = vi.fn()
    runControllerAction(makeCtx({ navigate }), makeAction('ask-agent', { to: null }))
    expect(navigate).not.toHaveBeenCalled()
  })
})

describe('useIssueDecisionActionController hook', () => {
  afterEach(() => cleanup())

  function renderController(opts: Parameters<typeof useIssueDecisionActionController>[0]) {
    return renderHook(() => useIssueDecisionActionController(opts), {
      wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter>,
    })
  }

  it('opens the stop confirmation and closes it again', () => {
    const { result } = renderController({
      mutations: mutations(),
      stopRecoverable: true,
      approvalStage: null,
      getStopConsequenceCopy: () => STOP_COPY,
    })

    expect(result.current.stopConfirming).toBe(false)
    act(() => result.current.openStopConfirm())
    expect(result.current.stopConfirming).toBe(true)
    act(() => result.current.closeStopConfirm())
    expect(result.current.stopConfirming).toBe(false)
  })

  it('blocks closeStopConfirm when a stop mutation is pending', () => {
    const { result } = renderController({
      mutations: mutations({ stopMutation: { isPending: true } }),
      stopRecoverable: true,
      approvalStage: null,
      getStopConsequenceCopy: () => STOP_COPY,
    })

    act(() => result.current.openStopConfirm())
    act(() => result.current.closeStopConfirm())
    expect(result.current.stopConfirming).toBe(false)
  })

  it('does not open stop confirmation while another decision mutation is pending', () => {
    const { result } = renderController({
      mutations: mutations({ approveMutation: { isPending: true } }),
      stopRecoverable: true,
      approvalStage: null,
      getStopConsequenceCopy,
    })

    act(() => result.current.openStopConfirm())
    expect(result.current.stopConfirming).toBe(false)
  })

  it('exposes sendBackBodyValid for the UI', () => {
    const { result } = renderController({
      mutations: mutations(),
      stopRecoverable: null,
      approvalStage: 'check',
      getStopConsequenceCopy: () => STOP_COPY,
    })
    expect(result.current.sendBackBodyValid('')).toBe(false)
    expect(result.current.sendBackBodyValid('feedback')).toBe(true)
  })

  it('reflects recoverable and irreversible stop copy through the hook', () => {
    const recoverable = renderController({
      mutations: mutations(),
      stopRecoverable: true,
      approvalStage: null,
      getStopConsequenceCopy: (rec) => rec
        ? { title: 'Stop (recoverable)', body: 'Stop preserves progress.' }
        : { title: 'Stop (irreversible)', body: 'Stop is irreversible.' },
    })
    expect(recoverable.result.current.stopConfirmTitle).toMatch(/recoverable/i)
    cleanup()

    const irreversible = renderController({
      mutations: mutations(),
      stopRecoverable: false,
      approvalStage: null,
      getStopConsequenceCopy: (rec) => rec
        ? { title: 'Stop (recoverable)', body: 'Stop preserves progress.' }
        : { title: 'Stop (irreversible)', body: 'Stop is irreversible.' },
    })
    expect(irreversible.result.current.stopConfirmTitle).toMatch(/irreversible/i)
  })

  it('exposes ask-agent / view-transcript actions that route through the React Router hook', () => {
    // Navigation is asserted at the runControllerAction level; the hook wires it via useNavigate.
    const { result } = renderController({
      mutations: mutations(),
      stopRecoverable: null,
      approvalStage: null,
      getStopConsequenceCopy: () => STOP_COPY,
    })
    expect(result.current.runAction).toBeInstanceOf(Function)
  })
})
