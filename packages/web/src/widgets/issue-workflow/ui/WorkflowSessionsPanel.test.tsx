import { describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { render } from '../../../../tests/test-utils'
import { WorkflowSessionsPanel } from './WorkflowSessionsPanel'
import { useWorkflowRunSessions, type WorkflowRunSession } from '../../../entities/coder-session'

vi.mock('../../../entities/coder-session', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/coder-session')>()),
  useWorkflowRunSessions: vi.fn(),
}))

const mockedUseWorkflowRunSessions = vi.mocked(useWorkflowRunSessions)

function session(overrides: Partial<WorkflowRunSession> & { usage?: Partial<NonNullable<WorkflowRunSession['usage']>>; eventSummary?: Partial<NonNullable<WorkflowRunSession['eventSummary']>> }): WorkflowRunSession {
  const { usage: usageOverride, eventSummary: eventSummaryOverride, ...rest } = overrides
  return {
    id: rest.id ?? 'session-1',
    workflowRunId: 'workflow-run-1',
    sessionName: rest.sessionName ?? 'check',
    acpSessionId: rest.acpSessionId ?? 'acp-1',
    projectId: 'project-1',
    issueNumber: 55,
    runnerId: 'runner-1',
    status: rest.status ?? 'completed',
    model: rest.model ?? 'minimax/MiniMax-M3',
    workDir: null,
    processPid: null,
    createdAt: rest.createdAt ?? '2026-06-12T10:00:00.000Z',
    startedAt: null,
    completedAt: rest.completedAt ?? null,
    lastDataAt: rest.lastDataAt ?? '2026-06-12T10:05:00.000Z',
    failureReason: rest.failureReason ?? null,
    exitCode: null,
    usage: usageOverride ?? undefined,
    eventSummary: eventSummaryOverride
      ? {
          resolvedModel: eventSummaryOverride.resolvedModel ?? null,
          failureCategory: eventSummaryOverride.failureCategory ?? null,
          toolCallCount: eventSummaryOverride.toolCallCount ?? null,
          toolErrorCount: eventSummaryOverride.toolErrorCount ?? null,
        }
      : undefined,
  }
}

describe('WorkflowSessionsPanel', () => {
  it('renders every session for the current workflow run with usage summary', () => {
    mockedUseWorkflowRunSessions.mockReturnValue({
      isLoading: false,
      sessions: [
        session({
          id: 's-check',
          sessionName: 'check',
          status: 'active',
          usage: {
            totalTokens: 588_371,
            costAmount: 0,
            costCurrency: 'USD',
            contextWindowUsed: 252_565,
            contextWindowSize: 512_000,
          },
          createdAt: '2026-06-12T10:02:00.000Z',
        }),
        session({
          id: 's-plan',
          sessionName: 'plan',
          status: 'completed',
          model: 'configured/PlanModel',
          eventSummary: { resolvedModel: 'resolved/PlanModel' },
          usage: {
            totalTokens: 42_000,
            contextWindowUsed: 32_000,
            contextWindowSize: 200_000,
          },
          createdAt: '2026-06-12T10:01:00.000Z',
        }),
        session({
          id: 's-build',
          sessionName: 'build',
          status: 'failed',
          usage: { totalTokens: 10_000 },
          failureReason: 'probe timed out',
          createdAt: '2026-06-12T10:03:00.000Z',
        }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    expect(mockedUseWorkflowRunSessions).toHaveBeenCalledWith('workflow-run-1')
    expect(screen.getByText('Sessions')).toBeInTheDocument()
    expect(screen.getByText(/3 sessions/)).toBeInTheDocument()
    expect(screen.getByText(/640\.4k processed/)).toBeInTheDocument()
    expect(screen.getByText(/peak 49% check/)).toBeInTheDocument()
    expect(screen.getByText('plan')).toBeInTheDocument()
    expect(screen.getByText('check')).toBeInTheDocument()
    expect(screen.getByText('build')).toBeInTheDocument()
    expect(screen.getAllByText('minimax/MiniMax-M3')).toHaveLength(2)
    expect(screen.getByText('configured/PlanModel -> resolved/PlanModel')).toBeInTheDocument()
    expect(screen.getByText('probe timed out')).toBeInTheDocument()
  })

  it('does not render without a workflow run id', () => {
    mockedUseWorkflowRunSessions.mockReturnValue({ isLoading: false, sessions: [] })

    const { container } = render(<WorkflowSessionsPanel issueNumber={55} workflowRunId={null} />)

    expect(container).toBeEmptyDOMElement()
  })
})
