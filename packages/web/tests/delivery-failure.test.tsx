import { describe, expect, it, vi } from 'vitest'
import { fireEvent, screen } from '@testing-library/react'
import {
  resolveDeliveryFailureFromMessage,
  resolveDeliveryFailureFromOutput,
  isDeliveryFailureKind,
  getDeliveryFailureGuidance,
  type DeliveryFailureKind,
} from '../src/shared/lib/delivery-failure'
import { WorkflowView } from '../src/widgets/issue-workflow/ui/WorkflowView'
import {
  IssueStatus,
  IssueHealth,
  WorkflowStage,
  type Issue,
  type WorkflowTimeline,
  useWorkflowTimeline,
} from '../src/entities/issue'
import { render } from './test-utils'

describe('delivery-failure guidance mapping', () => {
  const expectedGuidance: Record<DeliveryFailureKind, { label: string; nextAction: string }> = {
    conflict: {
      label: 'Conflict needs attention',
      nextAction: 'Conflicts could not be resolved automatically. Inspect the conflicting files, resolve them on the issue branch, and rerun prepare.',
    },
    'base-moved': {
      label: 'Base branch moved',
      nextAction: 'The base branch moved during publish. Prepare the branch again, then publish.',
    },
    'retry-safe': {
      label: 'Transient failure',
      nextAction: 'Retry the task — the failure is unrelated to conflicts or base movement.',
    },
  }

  it('recognises all three delivery failure kinds', () => {
    for (const kind of Object.keys(expectedGuidance) as DeliveryFailureKind[]) {
      expect(isDeliveryFailureKind(kind)).toBe(true)
      expect(isDeliveryFailureKind(`not-${kind}`)).toBe(false)
      expect(isDeliveryFailureKind(null)).toBe(false)
      expect(isDeliveryFailureKind(undefined)).toBe(false)
      expect(isDeliveryFailureKind(42)).toBe(false)
    }
  })

  it('exposes a guidance record for each delivery failure kind', () => {
    for (const kind of Object.keys(expectedGuidance) as DeliveryFailureKind[]) {
      const guidance = getDeliveryFailureGuidance(kind)
      expect(guidance.failureKind).toBe(kind)
      expect(guidance.label).toBe(expectedGuidance[kind].label)
      expect(guidance.nextAction).toBe(expectedGuidance[kind].nextAction)
    }
  })

  describe('resolveDeliveryFailureFromMessage', () => {
    it('extracts the conflict kind from a prepare failure message', () => {
      const result = resolveDeliveryFailureFromMessage('Prepare failed (conflict): CONFLICT in foo.ts')
      expect(result.failureKind).toBe<DeliveryFailureKind>('conflict')
      expect(result.guidance).toEqual({
        failureKind: 'conflict',
        label: expectedGuidance.conflict.label,
        nextAction: expectedGuidance.conflict.nextAction,
      })
    })

    it('extracts the base-moved kind from a publish failure message', () => {
      const result = resolveDeliveryFailureFromMessage('Publish failed (base-moved): non-fast-forward')
      expect(result.failureKind).toBe<DeliveryFailureKind>('base-moved')
      expect(result.guidance?.label).toBe(expectedGuidance['base-moved'].label)
      expect(result.guidance?.nextAction).toBe(expectedGuidance['base-moved'].nextAction)
    })

    it('extracts the retry-safe kind from a transient failure message', () => {
      const result = resolveDeliveryFailureFromMessage('Publish failed (retry-safe): network reset')
      expect(result.failureKind).toBe<DeliveryFailureKind>('retry-safe')
      expect(result.guidance?.label).toBe(expectedGuidance['retry-safe'].label)
      expect(result.guidance?.nextAction).toBe(expectedGuidance['retry-safe'].nextAction)
    })

    it('returns null guidance when no kind is present in the message', () => {
      const result = resolveDeliveryFailureFromMessage('Some unrelated failure text')
      expect(result.failureKind).toBeNull()
      expect(result.guidance).toBeNull()
    })

    it('returns null guidance when the message is empty', () => {
      expect(resolveDeliveryFailureFromMessage(null)).toEqual({ failureKind: null, guidance: null })
      expect(resolveDeliveryFailureFromMessage(undefined)).toEqual({ failureKind: null, guidance: null })
      expect(resolveDeliveryFailureFromMessage('')).toEqual({ failureKind: null, guidance: null })
    })
  })

  describe('resolveDeliveryFailureFromOutput', () => {
    it('extracts the kind from a parsed prepare JSON output', () => {
      const output = JSON.stringify({
        kind: 'prepare',
        status: 'failed',
        failureKind: 'conflict',
        conflicts: ['a.ts'],
      })
      const result = resolveDeliveryFailureFromOutput(output)
      expect(result.failureKind).toBe<DeliveryFailureKind>('conflict')
      expect(result.guidance?.label).toBe(expectedGuidance.conflict.label)
    })

    it('extracts the kind from a publish JSON object', () => {
      const result = resolveDeliveryFailureFromOutput({
        kind: 'publish',
        failureKind: 'base-moved',
      })
      expect(result.failureKind).toBe<DeliveryFailureKind>('base-moved')
      expect(result.guidance?.label).toBe(expectedGuidance['base-moved'].label)
    })

    it('extracts the kind from a nested object with .output field', () => {
      const result = resolveDeliveryFailureFromOutput({
        output: JSON.stringify({ failureKind: 'retry-safe' }),
      })
      expect(result.failureKind).toBe<DeliveryFailureKind>('retry-safe')
    })

    it('falls back to a nested message when no failureKind is present', () => {
      const result = resolveDeliveryFailureFromOutput({ message: 'Prepare failed (conflict): foo' })
      expect(result.failureKind).toBe<DeliveryFailureKind>('conflict')
    })

    it('returns null guidance when neither kind nor message is present', () => {
      expect(resolveDeliveryFailureFromOutput(null)).toEqual({ failureKind: null, guidance: null })
      expect(resolveDeliveryFailureFromOutput({ kind: 'prepare' })).toEqual({ failureKind: null, guidance: null })
    })

    it('ignores an unknown failure kind value', () => {
      const result = resolveDeliveryFailureFromOutput({ failureKind: 'something-else' })
      expect(result.failureKind).toBeNull()
      expect(result.guidance).toBeNull()
    })
  })
})

vi.mock('../src/entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/entities/issue')>()
  return {
    ...actual,
    useWorkflowTimeline: vi.fn(),
  }
})

const mockedUseWorkflowTimeline = vi.mocked(useWorkflowTimeline)

function makeFailureTimeline(kind: DeliveryFailureKind): WorkflowTimeline {
  const taskId = `integrate:${kind === 'base-moved' ? 'publish' : 'prepare'}.1`
  const title = kind === 'base-moved' ? 'Publish changes' : 'Prepare branch'
  const uses = kind === 'base-moved' ? 'mohist/publish' : 'mohist/prepare'
  const failureMessage =
    kind === 'conflict'
      ? 'Prepare failed (conflict): CONFLICT in foo.ts'
      : kind === 'base-moved'
        ? 'Publish failed (base-moved): non-fast-forward'
        : 'Prepare failed (retry-safe): network reset'
  return {
    workflowRunId: 'workflow-run-1',
    status: 'failed',
    currentStage: WorkflowStage.Integrate,
    pendingWork: null,
    stages: [
      {
        stage: WorkflowStage.Integrate,
        status: 'failed',
        order: 4,
        startedAt: '2026-01-01T00:00:00.000Z',
        completedAt: '2026-01-01T00:01:00.000Z',
        durationMs: 60000,
        tasks: [
          {
            id: taskId,
            title,
            uses,
            status: 'failed',
            startedAt: '2026-01-01T00:00:00.000Z',
            completedAt: '2026-01-01T00:01:00.000Z',
            durationMs: 60000,
            attempts: 1,
            message: failureMessage,
          },
        ],
        checks: [],
        approval: null,
      },
    ],
    availableActions: [],
  }
}

function makeBlockedIssue(blockedReason: string | undefined): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Split Integrate delivery',
    body: '',
    status: IssueStatus.InProgress,
    workflowStage: WorkflowStage.Integrate,
    health: IssueHealth.Blocked,
    blockedReason,
    projectId: 'test-project',
    labels: [],
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    comments: [],
    isDraft: false,
    canStart: false,
    blocker: null,
  }
}

describe('WorkflowView delivery failure rendering', () => {
  it('shows the conflict kind, label, and next action when prepare fails with a conflict', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeFailureTimeline('conflict') } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeBlockedIssue(undefined)} />)

    const taskButton = screen.getByRole('button', { name: /Prepare branch/ })
    fireEvent.click(taskButton)

    expect(await screen.findByText('Failure kind')).toBeInTheDocument()
    expect(screen.getByText('conflict')).toBeInTheDocument()
    expect(screen.getByText('Conflict needs attention')).toBeInTheDocument()
    expect(
      screen.getByText(/Inspect the conflicting files, resolve them on the issue branch/),
    ).toBeInTheDocument()
  })

  it('shows the base-moved kind, label, and next action when publish fails because the base moved', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeFailureTimeline('base-moved') } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeBlockedIssue(undefined)} />)

    const taskButton = screen.getByRole('button', { name: /Publish changes/ })
    fireEvent.click(taskButton)

    expect(await screen.findByText('Failure kind')).toBeInTheDocument()
    expect(screen.getByText('base-moved')).toBeInTheDocument()
    expect(screen.getByText('Base branch moved')).toBeInTheDocument()
    expect(screen.getByText(/Prepare the branch again, then publish/)).toBeInTheDocument()
  })

  it('shows the retry-safe kind, label, and next action for transient delivery failures', async () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: makeFailureTimeline('retry-safe') } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeBlockedIssue(undefined)} />)

    const taskButton = screen.getByRole('button', { name: /Prepare branch/ })
    fireEvent.click(taskButton)

    expect(await screen.findByText('Failure kind')).toBeInTheDocument()
    expect(screen.getByText('retry-safe')).toBeInTheDocument()
    expect(screen.getByText('Transient failure')).toBeInTheDocument()
    expect(screen.getByText(/Retry the task/)).toBeInTheDocument()
  })

  it('does not show a delivery failure banner for non-delivery task failures', () => {
    const timeline: WorkflowTimeline = {
      workflowRunId: 'workflow-run-2',
      status: 'failed',
      currentStage: WorkflowStage.Build,
      pendingWork: null,
      stages: [
        {
          stage: WorkflowStage.Build,
          status: 'failed',
          order: 2,
          startedAt: '2026-01-01T00:00:00.000Z',
          completedAt: '2026-01-01T00:01:00.000Z',
          durationMs: 60000,
          tasks: [
            {
              id: 'build-task-1',
              title: 'Implement WorkflowView',
              uses: 'mohist/coder-agent',
              status: 'failed',
              startedAt: '2026-01-01T00:00:00.000Z',
              completedAt: '2026-01-01T00:01:00.000Z',
              durationMs: 60000,
              attempts: 1,
              message: 'agent crashed',
            },
          ],
          checks: [],
          approval: null,
        },
      ],
      availableActions: [],
    }
    mockedUseWorkflowTimeline.mockReturnValue({ data: timeline } as ReturnType<typeof useWorkflowTimeline>)

    render(<WorkflowView issue={makeBlockedIssue('Build failed: agent crashed')} />)

    expect(screen.queryByText('Failure kind')).not.toBeInTheDocument()
  })

  it('surfaces the kind and next action in the Integrate failure panel from the blocked reason', () => {
    mockedUseWorkflowTimeline.mockReturnValue({ data: undefined } as ReturnType<typeof useWorkflowTimeline>)

    render(
      <WorkflowView
        issue={makeBlockedIssue('Prepare failed (base-moved): non-fast-forward')}
      />,
    )

    expect(screen.getByText('Integration Failed')).toBeInTheDocument()
    expect(screen.getByText('Prepare branch')).toBeInTheDocument()
    expect(screen.getByText('base-moved')).toBeInTheDocument()
    expect(screen.getByText('Base branch moved')).toBeInTheDocument()
    expect(screen.getByText(/Prepare the branch again, then publish/)).toBeInTheDocument()
  })
})
