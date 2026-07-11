import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { RuntimeDecisionSurface, type RuntimeDecisionSurfaceMutations } from './RuntimeDecisionSurface'
import type { DecisionEvidence } from './RuntimeDecisionSurface'
import type { RuntimeDecision } from '../model/derive-runtime-decision'
import type { WorkflowArtifact, WorkflowArtifactDirectory } from '../../../entities/issue'
import type { ArtifactContentHook, ArtifactOpenerArtifactsHook } from './ArtifactOpener'

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
