// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { RuntimeDecisionSurface, type RuntimeDecisionSurfaceMutations } from './RuntimeDecisionSurface'
import type { DecisionEvidence, DriftRecoveryAction, ExecutionSignal } from './RuntimeDecisionSurface'
import type { RuntimeDecision } from '../model/derive-runtime-decision'
import type { WorkflowArtifact, WorkflowArtifactDirectory } from '../../../entities/issue'
import type { ArtifactContentHook, ArtifactOpenerArtifactsHook } from './ArtifactOpener'
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

describe('RuntimeDecisionSurface', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders exactly one primary action from the supplied decision', () => {
    render(<RuntimeDecisionSurface decision={decision()} mutations={mutations()} />)

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.dataset.summary).toBe('running')
    expect(surface.className).toContain('bg-card')
    expect(surface.className).toContain('border-l-info')

    const primaryActions = within(surface).getAllByRole('button')
      .filter((button) => button.getAttribute('data-primary') === 'true')
    expect(primaryActions).toHaveLength(1)
    expect(primaryActions[0]).toHaveAttribute('data-testid', 'runtime-action-stop')
  })

  it('uses shared pending state for the matching primary action', () => {
    render(
      <RuntimeDecisionSurface
        decision={decision()}
        mutations={mutations({ forceStopMutation: mutation<RuntimeDecisionSurfaceMutations['forceStopMutation']>({ isPending: true }) })}
      />,
    )

    const stop = screen.getByTestId('runtime-action-stop')
    expect(stop).toBeDisabled()
    expect(stop).toHaveTextContent('Stopping...')
  })

  it('routes a recoverable Stop through forceStopMutation after confirmation copy is shown', () => {
    const forceStopMutation = mutation()
    const stopMutation = mutation()
    render(
      <RuntimeDecisionSurface
        decision={decision({ stopRecoverable: true })}
        mutations={mutations({ forceStopMutation, stopMutation })}
      />,
    )

    const stop = screen.getByTestId('runtime-action-stop')
    fireEvent.click(stop)
    expect(screen.getByTestId('runtime-stop-confirmation-copy')).toHaveTextContent('preserve progress')
    expect(forceStopMutation.mutate).not.toHaveBeenCalled()

    fireEvent.click(stop)
    expect(forceStopMutation.mutate).toHaveBeenCalledTimes(1)
    expect(stopMutation.mutate).not.toHaveBeenCalled()
  })

  it('routes a terminal Stop through stopMutation after irreversible confirmation copy is shown', () => {
    const forceStopMutation = mutation()
    const stopMutation = mutation()
    render(
      <RuntimeDecisionSurface
        decision={decision({ stopRecoverable: false })}
        mutations={mutations({ forceStopMutation, stopMutation })}
      />,
    )

    const stop = screen.getByTestId('runtime-action-stop')
    fireEvent.click(stop)
    expect(screen.getByTestId('runtime-stop-confirmation-copy')).toHaveTextContent('irreversible')

    fireEvent.click(stop)
    expect(stopMutation.mutate).toHaveBeenCalledTimes(1)
    expect(forceStopMutation.mutate).not.toHaveBeenCalled()
  })

  it('shows stop consequence copy when Stop is a secondary visible action', () => {
    const stop = { kind: 'stop' as const, label: 'Stop', enabled: true }
    const retry = { kind: 'retry' as const, label: 'Retry', enabled: true }
    render(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'blocked',
          primary: retry,
          actions: [retry, stop],
          stopRecoverable: true,
        })}
        mutations={mutations()}
      />,
    )

    fireEvent.click(screen.getByTestId('runtime-action-stop'))

    expect(screen.getByTestId('runtime-stop-confirmation-copy')).toHaveTextContent('preserve progress')
  })

  it('renders inspect as disabled even if a decision accidentally marks it enabled', () => {
    const inspect = { kind: 'inspect' as const, label: 'View transcript', enabled: true }
    render(
      <RuntimeDecisionSurface
        decision={decision({ primary: null, actions: [inspect] })}
        mutations={mutations()}
      />,
    )

    const inspectButton = screen.getByTestId('runtime-action-inspect')
    expect(inspectButton).toBeDisabled()
    expect(inspectButton.getAttribute('title')).toMatch(/transcript navigation/i)
  })

  it('collects feedback text before sending back an approval', () => {
    const sendBackMutation = mutation<RuntimeDecisionSurfaceMutations['sendBackMutation']>()
    const sendBack = { kind: 'send-back' as const, label: 'Send back', enabled: true }
    render(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'approval-required',
          primary: sendBack,
          actions: [sendBack],
          approvalStage: 'check',
        })}
        mutations={mutations({ sendBackMutation })}
      />,
    )

    fireEvent.click(screen.getByTestId('runtime-action-send-back'))

    expect(screen.getByTestId('runtime-send-back-form')).toBeInTheDocument()
    expect(screen.getByTestId('runtime-submit-send-back')).toBeDisabled()
    fireEvent.change(screen.getByTestId('runtime-send-back-textarea'), {
      target: { value: 'Please address the verification failure.' },
    })
    fireEvent.click(screen.getByTestId('runtime-submit-send-back'))

    expect(sendBackMutation.mutate).toHaveBeenCalledWith(
      { stage: 'check', body: 'Please address the verification failure.' },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    )
  })
})

function makeArtifact(overrides: Partial<WorkflowArtifact> = {}): WorkflowArtifact {
  return {
    artifactId: 'art-1',
    workflowRunId: 'wr-1',
    taskRunId: 'plan.1',
    path: 'plan.md',
    kind: 'file',
    contentType: 'text/markdown',
    size: 12,
    recordedAt: '2026-01-01T00:00:00.000Z',
    displayName: 'plan.md',
    ...overrides,
  }
}

function buildEvidence(opts: {
  artifacts: Array<WorkflowArtifact | WorkflowArtifactDirectory>
}): {
  evidence: DecisionEvidence
  artifactsHook: ArtifactOpenerArtifactsHook
  contentHook: ArtifactContentHook
} {
  const artifactsHook: ArtifactOpenerArtifactsHook = () => ({
    data: opts.artifacts,
    isLoading: false,
    error: null,
  })
  const contentHook: ArtifactContentHook = () => ({
    data: { kind: 'text', content: '# Plan', contentType: 'text/markdown' },
    isLoading: false,
    error: null,
  })
  return {
    evidence: {
      issueNumber: 14,
      workflowRunId: 'wr-1',
      artifactsHook,
      contentHook,
      compactLimit: 3,
    },
    artifactsHook,
    contentHook,
  }
}

describe('RuntimeDecisionSurface decision-adjacent evidence slot', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders an openable plan/check artifact list inside the surface during an approval decision', () => {
    const approve = { kind: 'approve' as const, label: 'Approve', enabled: true }
    const sendBack = { kind: 'send-back' as const, label: 'Send back', enabled: true }
    const { evidence } = buildEvidence({
      artifacts: [
        makeArtifact({ artifactId: 'art-plan', path: 'plan.md', displayName: 'plan.md' }),
      ],
    })

    render(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'approval-required',
          primary: approve,
          actions: [approve, sendBack],
          approvalStage: 'check',
        })}
        mutations={mutations()}
        evidence={evidence}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    const evidenceBlock = within(surface).getByTestId('runtime-evidence')
    expect(evidenceBlock.dataset.summary).toBe('approval-required')
    const list = within(evidenceBlock).getByTestId('runtime-evidence-list')
    expect(list.dataset.mode).toBe('compact')
    expect(within(list).getByText('plan.md')).toBeInTheDocument()
    const items = within(list).getAllByTestId('latest-artifact-item')
    expect(items).toHaveLength(1)
  })

  it('opens the ArtifactContentViewer modal from a compact in-surface artifact item', () => {
    const approve = { kind: 'approve' as const, label: 'Approve', enabled: true }
    const sendBack = { kind: 'send-back' as const, label: 'Send back', enabled: true }
    const { evidence } = buildEvidence({
      artifacts: [
        makeArtifact({ artifactId: 'art-review', path: 'review.md', displayName: 'review.md' }),
      ],
    })

    render(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'approval-required',
          primary: approve,
          actions: [approve, sendBack],
          approvalStage: 'check',
        })}
        mutations={mutations()}
        evidence={evidence}
      />,
    )

    fireEvent.click(screen.getByText('review.md'))

    expect(screen.getByTestId('markdown-reader')).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 2, name: 'Plan' })).toBeInTheDocument()
  })

  it('renders the evidence slot during a blocked recovery decision', () => {
    const retry = { kind: 'retry' as const, label: 'Retry', enabled: true }
    const { evidence } = buildEvidence({
      artifacts: [
        makeArtifact({ artifactId: 'art-check', path: 'check.log', displayName: 'check.log' }),
      ],
    })

    render(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'blocked',
          primary: retry,
          actions: [retry],
        })}
        mutations={mutations()}
        evidence={evidence}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    const evidenceBlock = within(surface).getByTestId('runtime-evidence')
    expect(evidenceBlock.dataset.summary).toBe('blocked')
    expect(within(evidenceBlock).getByText('check.log')).toBeInTheDocument()
  })

  it('renders the evidence slot during a failed recovery decision', () => {
    const retry = { kind: 'retry' as const, label: 'Retry', enabled: true }
    const { evidence } = buildEvidence({
      artifacts: [
        makeArtifact({ artifactId: 'art-review', path: 'review.md', displayName: 'review.md' }),
      ],
    })

    render(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'failed',
          primary: retry,
          actions: [retry],
        })}
        mutations={mutations()}
        evidence={evidence}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    const evidenceBlock = within(surface).getByTestId('runtime-evidence')
    expect(evidenceBlock.dataset.summary).toBe('failed')
    expect(within(evidenceBlock).getByText('review.md')).toBeInTheDocument()
  })

  it('does not render the evidence slot during a running decision', () => {
    const { evidence } = buildEvidence({
      artifacts: [
        makeArtifact({ artifactId: 'art-plan', path: 'plan.md', displayName: 'plan.md' }),
      ],
    })

    render(
      <RuntimeDecisionSurface
        decision={decision({ summary: 'running' })}
        mutations={mutations()}
        evidence={evidence}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(within(surface).queryByTestId('runtime-evidence')).toBeNull()
    expect(within(surface).queryByTestId('runtime-evidence-list')).toBeNull()
  })

  it('does not render the evidence slot during a queued decision', () => {
    const { evidence } = buildEvidence({
      artifacts: [
        makeArtifact({ artifactId: 'art-plan', path: 'plan.md', displayName: 'plan.md' }),
      ],
    })

    render(
      <RuntimeDecisionSurface
        decision={decision({ summary: 'queued', primary: null, actions: [] })}
        mutations={mutations()}
        evidence={evidence}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(within(surface).queryByTestId('runtime-evidence')).toBeNull()
  })

  it('does not render the evidence slot during a done decision', () => {
    const { evidence } = buildEvidence({
      artifacts: [
        makeArtifact({ artifactId: 'art-plan', path: 'plan.md', displayName: 'plan.md' }),
      ],
    })

    render(
      <RuntimeDecisionSurface
        decision={decision({ summary: 'done', primary: null, actions: [] })}
        mutations={mutations()}
        evidence={evidence}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(within(surface).queryByTestId('runtime-evidence')).toBeNull()
  })

  it('does not render the evidence slot when the evidence workflowRunId is absent', () => {
    render(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'approval-required',
          primary: { kind: 'approve', label: 'Approve', enabled: true },
          actions: [{ kind: 'approve', label: 'Approve', enabled: true }],
          approvalStage: 'check',
        })}
        mutations={mutations()}
        evidence={{
          issueNumber: 14,
          workflowRunId: null,
        }}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(within(surface).queryByTestId('runtime-evidence')).toBeNull()
  })

  it('limits the in-surface evidence list to the configured compactLimit', () => {
    const { evidence } = buildEvidence({
      artifacts: [
        makeArtifact({ artifactId: 'a1', path: 'plan.md', displayName: 'plan.md' }),
        makeArtifact({ artifactId: 'a2', path: 'review.md', displayName: 'review.md' }),
        makeArtifact({ artifactId: 'a3', path: 'check.log', displayName: 'check.log' }),
        makeArtifact({ artifactId: 'a4', path: 'summary.md', displayName: 'summary.md' }),
        makeArtifact({ artifactId: 'a5', path: 'extra.md', displayName: 'extra.md' }),
      ],
    })

    render(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'approval-required',
          primary: { kind: 'approve', label: 'Approve', enabled: true },
          actions: [{ kind: 'approve', label: 'Approve', enabled: true }],
          approvalStage: 'check',
        })}
        mutations={mutations()}
        evidence={evidence}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    const list = within(surface).getByTestId('runtime-evidence-list')
    const items = within(list).getAllByTestId('latest-artifact-item')
    expect(items).toHaveLength(3)
    expect(within(list).getByText('plan.md')).toBeInTheDocument()
    expect(within(list).getByText('review.md')).toBeInTheDocument()
    expect(within(list).getByText('check.log')).toBeInTheDocument()
    expect(within(list).queryByText('summary.md')).toBeNull()
    expect(within(list).queryByText('extra.md')).toBeNull()
  })

  it('does not render the evidence slot when there are no artifacts even with a workflowRunId', () => {
    const { evidence } = buildEvidence({ artifacts: [] })

    render(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'approval-required',
          primary: { kind: 'approve', label: 'Approve', enabled: true },
          actions: [{ kind: 'approve', label: 'Approve', enabled: true }],
          approvalStage: 'check',
        })}
        mutations={mutations()}
        evidence={evidence}
      />,
    )

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(within(surface).queryByTestId('runtime-evidence')).toBeNull()
  })
})

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
