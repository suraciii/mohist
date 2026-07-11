import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { RuntimeDecisionSurface, type RuntimeDecisionSurfaceMutations } from './RuntimeDecisionSurface'
import type { DriftRecoveryAction, ExecutionSignal } from './RuntimeDecisionSurface'
import type { RuntimeDecision } from '../model/derive-runtime-decision'
import { render as renderWithRouter } from '../../../../tests/test-utils'

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
  const stop = { kind: 'stop' as const, label: 'Stop', enabled: true }
  return {
    summary: 'running',
    headline: 'Workflow running (Build)',
    rationale: 'The workflow is currently executing.',
    currentTask: null,
    nextAction: 'No user action required right now.',
    primary: stop,
    actions: [stop, { kind: 'inspect', label: 'View transcript', enabled: false }],
    stopRecoverable: true,
    waitReason: null,
    driftNote: null,
    blockedReason: null,
    approvalStage: null,
    ...overrides,
  }
}

describe('RuntimeDecisionSurface executionSignal slot', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders a compact session signal inside the surface when an active session is provided', () => {
    const executionSignal: ExecutionSignal = {
      activeSession: {
        sessionName: 'review-repair',
        transcriptPath: '/test-project/issues/14/workflow/sessions/review-repair',
      },
    }

    renderWithRouter(
      <RuntimeDecisionSurface
        decision={decision({ summary: 'running' })}
        mutations={mutations()}
        executionSignal={executionSignal}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    const signal = within(surface).getByTestId('runtime-execution-signal')
    expect(signal).toBeInTheDocument()

    const sessionSpan = within(signal).getByTestId('runtime-execution-signal-session')
    expect(sessionSpan.dataset.sessionName).toBe('review-repair')
    const link = within(sessionSpan).getByTestId('runtime-execution-signal-session-link')
    expect(link).toHaveAttribute('href', '/test-project/issues/14/workflow/sessions/review-repair')
    expect(link).toHaveTextContent('review-repair')

    expect(within(signal).queryByTestId('runtime-execution-signal-runner')).toBeNull()
  })

  it('renders the runner-unavailable reason inside the surface when the runner gates the decision', () => {
    const executionSignal: ExecutionSignal = {
      runnerGating: {
        kind: 'runner-unavailable',
        reason: 'No runner is connected. Start a runner before this issue can run.',
      },
    }

    renderWithRouter(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'queued',
          waitReason: 'No runner is connected. Start a runner before this issue can run.',
          primary: null,
          actions: [],
        })}
        mutations={mutations()}
        executionSignal={executionSignal}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    const signal = within(surface).getByTestId('runtime-execution-signal')
    const runner = within(signal).getByTestId('runtime-execution-signal-runner')
    expect(runner.dataset.gatingKind).toBe('runner-unavailable')
    expect(runner).toHaveTextContent('No runner is connected.')

    expect(within(signal).queryByTestId('runtime-execution-signal-session')).toBeNull()
  })

  it('renders the capacity-full reason inside the surface when runner capacity is full', () => {
    const executionSignal: ExecutionSignal = {
      runnerGating: {
        kind: 'capacity-full',
        reason: 'Runner capacity is full (2/2).',
      },
    }

    renderWithRouter(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'queued',
          waitReason: 'Runner capacity is full (2/2).',
          primary: null,
          actions: [],
        })}
        mutations={mutations()}
        executionSignal={executionSignal}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    const signal = within(surface).getByTestId('runtime-execution-signal')
    const runner = within(signal).getByTestId('runtime-execution-signal-runner')
    expect(runner.dataset.gatingKind).toBe('capacity-full')
    expect(runner).toHaveTextContent('Runner capacity is full (2/2).')
  })

  it('renders both session and runner-gating signals together when both apply', () => {
    const executionSignal: ExecutionSignal = {
      activeSession: {
        sessionName: 'build-task',
        transcriptPath: '/test-project/issues/14/workflow/sessions/build-task',
      },
      runnerGating: {
        kind: 'capacity-full',
        reason: 'Runner capacity is full (4/4).',
      },
    }

    renderWithRouter(
      <RuntimeDecisionSurface
        decision={decision({ summary: 'running' })}
        mutations={mutations()}
        executionSignal={executionSignal}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    const signal = within(surface).getByTestId('runtime-execution-signal')
    expect(within(signal).getByTestId('runtime-execution-signal-session')).toBeInTheDocument()
    expect(within(signal).getByTestId('runtime-execution-signal-runner')).toBeInTheDocument()
  })

  it('omits the execution signal when the prop is omitted', () => {
    renderWithRouter(
      <RuntimeDecisionSurface
        decision={decision({ summary: 'running' })}
        mutations={mutations()}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(within(surface).queryByTestId('runtime-execution-signal')).toBeNull()
  })

  it('omits the execution signal when executionSignal has no activeSession and no runnerGating', () => {
    renderWithRouter(
      <RuntimeDecisionSurface
        decision={decision({ summary: 'running' })}
        mutations={mutations()}
        executionSignal={{ activeSession: null, runnerGating: null }}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(within(surface).queryByTestId('runtime-execution-signal')).toBeNull()
  })

  it('omits the execution signal for backlog/done paths when no session is active and runner is not gating', () => {
    renderWithRouter(
      <RuntimeDecisionSurface
        decision={decision({ summary: 'queued', waitReason: null, primary: null, actions: [] })}
        mutations={mutations()}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(within(surface).queryByTestId('runtime-execution-signal')).toBeNull()
  })

  it('uses an external runnerMessage when supplied as the runner-unavailable reason', () => {
    const executionSignal: ExecutionSignal = {
      runnerGating: {
        kind: 'runner-unavailable',
        reason: 'Runner has been offline for 12 minutes. Restart it from the runner settings.',
      },
    }

    renderWithRouter(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'queued',
          waitReason: 'Runner has been offline for 12 minutes. Restart it from the runner settings.',
          primary: null,
          actions: [],
        })}
        mutations={mutations()}
        executionSignal={executionSignal}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    const runner = within(surface).getByTestId('runtime-execution-signal-runner')
    expect(runner).toHaveTextContent('Runner has been offline for 12 minutes.')
  })

  it('renders the session link inside the runtime-decision-surface section, alongside other surface content', () => {
    const executionSignal: ExecutionSignal = {
      activeSession: {
        sessionName: 'check-recovery',
        transcriptPath: '/test-project/issues/14/workflow/sessions/check-recovery',
      },
    }

    renderWithRouter(
      <RuntimeDecisionSurface
        decision={decision({ summary: 'running' })}
        mutations={mutations()}
        executionSignal={executionSignal}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.contains(screen.getByTestId('runtime-execution-signal'))).toBe(true)
    expect(surface.contains(screen.getByTestId('runtime-action-stop'))).toBe(true)
  })
})

function makeDriftRecovery(overrides: Partial<DriftRecoveryAction> = {}): DriftRecoveryAction {
  return {
    baseBranch: 'master',
    branch: 'mohist/run-wr-14',
    trigger: vi.fn(),
    isPending: false,
    isQueued: false,
    isRebasing: false,
    isConflictResolving: false,
    isConflictFailed: false,
    canRequest: true,
    hasConflicts: null,
    error: null,
    ...overrides,
  }
}

describe('RuntimeDecisionSurface driftRecovery slot', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders a compact drift-recovery block inside the surface when drift needs attention', () => {
    const driftRecovery = makeDriftRecovery()

    render(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'running',
          driftNote: 'Base drift requires attention.',
        })}
        mutations={mutations()}
        driftRecovery={driftRecovery}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    const recovery = within(surface).getByTestId('runtime-drift-recovery')
    expect(recovery).toBeInTheDocument()
    expect(recovery.dataset.summary).toBe('running')

    const action = within(recovery).getByTestId('runtime-drift-recovery-action')
    expect(action).toHaveTextContent('Rebase onto master')
    expect(action).not.toBeDisabled()
  })

  it('omits the drift-recovery block when the prop is absent', () => {
    render(
      <RuntimeDecisionSurface
        decision={decision({ driftNote: 'Base drift requires attention.' })}
        mutations={mutations()}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(within(surface).queryByTestId('runtime-drift-recovery')).toBeNull()
  })

  it('omits the drift-recovery block when decision.driftNote is null even with a driftRecovery prop', () => {
    const driftRecovery = makeDriftRecovery()

    render(
      <RuntimeDecisionSurface
        decision={decision({ driftNote: null })}
        mutations={mutations()}
        driftRecovery={driftRecovery}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(within(surface).queryByTestId('runtime-drift-recovery')).toBeNull()
  })

  it('does not block the existing primary action buttons when drift recovery is shown', () => {
    const driftRecovery = makeDriftRecovery()
    const stop = { kind: 'stop' as const, label: 'Stop', enabled: true }
    const retry = { kind: 'retry' as const, label: 'Retry', enabled: true }

    render(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'blocked',
          primary: retry,
          actions: [retry, stop],
          driftNote: 'Base drift requires attention.',
        })}
        mutations={mutations()}
        driftRecovery={driftRecovery}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(within(surface).getByTestId('runtime-drift-recovery')).toBeInTheDocument()
    expect(within(surface).getByTestId('runtime-action-retry')).toBeInTheDocument()
    expect(within(surface).getByTestId('runtime-action-stop')).toBeInTheDocument()
  })

  it('disables the rebase button when canRequest is false and shows a queued label when isQueued is true', () => {
    const driftRecovery = makeDriftRecovery({ canRequest: false, isQueued: true })

    render(
      <RuntimeDecisionSurface
        decision={decision({ driftNote: 'Base drift requires attention.' })}
        mutations={mutations()}
        driftRecovery={driftRecovery}
      />,
    )

    const recovery = screen.getByTestId('runtime-drift-recovery')
    const action = within(recovery).getByTestId('runtime-drift-recovery-action')
    expect(action).toBeDisabled()
    expect(action).toHaveTextContent('Rebase queued')
  })

  it('renders conflict info inside the drift-recovery block when hasConflicts is set', () => {
    const driftRecovery = makeDriftRecovery({
      hasConflicts: ['packages/server/src/foo.ts'],
    })

    render(
      <RuntimeDecisionSurface
        decision={decision({ driftNote: 'Base drift requires attention.' })}
        mutations={mutations()}
        driftRecovery={driftRecovery}
      />,
    )

    const recovery = screen.getByTestId('runtime-drift-recovery')
    expect(within(recovery).getByText(/Conflicting files/i)).toBeInTheDocument()
    expect(within(recovery).getByText('packages/server/src/foo.ts')).toBeInTheDocument()
  })

  it('shows a conflict-resolution-failed banner when isConflictFailed is true', () => {
    const driftRecovery = makeDriftRecovery({
      isConflictFailed: true,
      hasConflicts: ['packages/server/src/foo.ts'],
    })

    render(
      <RuntimeDecisionSurface
        decision={decision({ driftNote: 'Base drift requires attention.' })}
        mutations={mutations()}
        driftRecovery={driftRecovery}
      />,
    )

    const recovery = screen.getByTestId('runtime-drift-recovery')
    expect(within(recovery).getByText(/Conflict resolution failed/i)).toBeInTheDocument()
  })

  it('shows the in-progress rebase label when isPending is true', () => {
    const driftRecovery = makeDriftRecovery({ isPending: true, canRequest: false })

    render(
      <RuntimeDecisionSurface
        decision={decision({ driftNote: 'Base drift requires attention.' })}
        mutations={mutations()}
        driftRecovery={driftRecovery}
      />,
    )

    const action = within(screen.getByTestId('runtime-drift-recovery')).getByTestId('runtime-drift-recovery-action')
    expect(action).toHaveTextContent('Rebasing...')
    expect(action).toBeDisabled()
  })

  it('triggers the shared mutation when the rebase action button is clicked', () => {
    const trigger = vi.fn()
    const driftRecovery = makeDriftRecovery({ trigger })

    render(
      <RuntimeDecisionSurface
        decision={decision({ driftNote: 'Base drift requires attention.' })}
        mutations={mutations()}
        driftRecovery={driftRecovery}
      />,
    )

    fireEvent.click(screen.getByTestId('runtime-drift-recovery-action'))
    expect(trigger).toHaveBeenCalledTimes(1)
  })

  it('does not render a runtime-action-rebase button (rebase is not a workflow lifecycle action)', () => {
    const driftRecovery = makeDriftRecovery()

    render(
      <RuntimeDecisionSurface
        decision={decision({ driftNote: 'Base drift requires attention.' })}
        mutations={mutations()}
        driftRecovery={driftRecovery}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(within(surface).queryByTestId('runtime-action-rebase')).toBeNull()
  })
})
