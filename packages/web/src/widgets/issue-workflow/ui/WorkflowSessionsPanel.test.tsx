import { describe, expect, it, vi } from 'vitest'
import { fireEvent, screen } from '@testing-library/react'
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

  it('renders a compact filter/sort control row above the session list', () => {
    mockedUseWorkflowRunSessions.mockReturnValue({
      isLoading: false,
      sessions: [
        session({ id: 's-plan', sessionName: 'plan', status: 'completed', createdAt: '2026-06-12T10:01:00.000Z' }),
        session({ id: 's-build', sessionName: 'build', status: 'failed', createdAt: '2026-06-12T10:03:00.000Z' }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    expect(screen.getByTestId('workflow-sessions-controls')).toBeInTheDocument()
    const statusFilter = screen.getByTestId('workflow-sessions-status-filter') as HTMLSelectElement
    const stageFilter = screen.getByTestId('workflow-sessions-stage-filter') as HTMLSelectElement
    const sortSelect = screen.getByTestId('workflow-sessions-sort') as HTMLSelectElement

    expect(statusFilter.value).toBe('')
    expect(stageFilter.value).toBe('')
    expect(sortSelect.value).toBe('createdAt')

    const stageOptions = Array.from(stageFilter.querySelectorAll('option')).map((o) => o.textContent)
    expect(stageOptions).toEqual(['All stages', 'Plan', 'Build', 'Check', 'Integrate'])
  })

  it('filtering by status hides non-matching sessions and surfaces a notice', () => {
    mockedUseWorkflowRunSessions.mockReturnValue({
      isLoading: false,
      sessions: [
        session({ id: 's-plan', sessionName: 'plan', status: 'completed', createdAt: '2026-06-12T10:01:00.000Z' }),
        session({ id: 's-build', sessionName: 'build', status: 'failed', createdAt: '2026-06-12T10:03:00.000Z' }),
        session({ id: 's-check', sessionName: 'check', status: 'running', createdAt: '2026-06-12T10:02:00.000Z' }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    const statusFilter = screen.getByTestId('workflow-sessions-status-filter') as HTMLSelectElement
    fireEvent.change(statusFilter, { target: { value: 'failed' } })

    expect(screen.queryByText('plan')).not.toBeInTheDocument()
    expect(screen.queryByText('check')).not.toBeInTheDocument()
    expect(screen.getByText('build')).toBeInTheDocument()
    expect(screen.getByTestId('workflow-sessions-filter-notice')).toHaveTextContent('Showing 1 of 3 sessions')
  })

  it('filtering by stage hides non-matching sessions', () => {
    mockedUseWorkflowRunSessions.mockReturnValue({
      isLoading: false,
      sessions: [
        session({ id: 's-plan', sessionName: 'plan', status: 'completed', createdAt: '2026-06-12T10:01:00.000Z' }),
        session({ id: 's-build', sessionName: 'build', status: 'completed', createdAt: '2026-06-12T10:03:00.000Z' }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    const stageFilter = screen.getByTestId('workflow-sessions-stage-filter') as HTMLSelectElement
    fireEvent.change(stageFilter, { target: { value: 'build' } })

    expect(screen.queryByText('plan')).not.toBeInTheDocument()
    expect(screen.getByText('build')).toBeInTheDocument()
  })

  it('sorting by tokens reorders visible sessions', () => {
    mockedUseWorkflowRunSessions.mockReturnValue({
      isLoading: false,
      sessions: [
        session({
          id: 's-plan',
          sessionName: 'plan',
          status: 'completed',
          createdAt: '2026-06-12T10:00:00.000Z',
          usage: { totalTokens: 1_000 },
        }),
        session({
          id: 's-build',
          sessionName: 'build',
          status: 'completed',
          createdAt: '2026-06-12T10:01:00.000Z',
          usage: { totalTokens: 5_000 },
        }),
        session({
          id: 's-check',
          sessionName: 'check',
          status: 'completed',
          createdAt: '2026-06-12T10:02:00.000Z',
          usage: { totalTokens: 2_500 },
        }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    const sortSelect = screen.getByTestId('workflow-sessions-sort') as HTMLSelectElement
    fireEvent.change(sortSelect, { target: { value: 'tokens' } })

    const links = screen.getAllByRole('link')
    const rendered = links.map((link) => link.getAttribute('href'))
    expect(rendered).toEqual([
      '/Test%20Project/issues/55/workflow/sessions/build',
      '/Test%20Project/issues/55/workflow/sessions/check',
      '/Test%20Project/issues/55/workflow/sessions/plan',
    ])
  })

  it('shows an empty-result message when filters hide every session', () => {
    mockedUseWorkflowRunSessions.mockReturnValue({
      isLoading: false,
      sessions: [
        session({ id: 's-plan', sessionName: 'plan', status: 'completed' }),
        session({ id: 's-build', sessionName: 'build', status: 'completed' }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    const stageFilter = screen.getByTestId('workflow-sessions-stage-filter') as HTMLSelectElement
    fireEvent.change(stageFilter, { target: { value: 'check' } })

    expect(screen.getByText(/No sessions match the current filters/)).toBeInTheDocument()
  })
})

describe('WorkflowSessionRow responsive layout', () => {
  it('applies min-w-0 to the row link and header so content can shrink', () => {
    mockedUseWorkflowRunSessions.mockReturnValue({
      isLoading: false,
      sessions: [
        session({
          id: 's-plan',
          sessionName: 'plan',
          status: 'completed',
          model: 'minimax/MiniMax-M3',
          createdAt: '2026-06-12T10:01:00.000Z',
        }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    const row = screen.getByTestId('workflow-session-row')
    expect(row.className).toContain('block')
    expect(row.className).toContain('min-w-0')

    const header = screen.getByTestId('workflow-session-row-header')
    expect(header.className).toContain('flex')
    expect(header.className).toContain('flex-wrap')
    expect(header.className).toContain('min-w-0')
  })

  it('truncates the session name span and lets the model badge wrap to a second line', () => {
    mockedUseWorkflowRunSessions.mockReturnValue({
      isLoading: false,
      sessions: [
        session({
          id: 's-plan',
          sessionName: 'plan',
          status: 'completed',
          model: 'configured/PlanModel',
          eventSummary: { resolvedModel: 'resolved/PlanModel-Long-Resolved-Model-Name' },
          createdAt: '2026-06-12T10:01:00.000Z',
        }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    const row = screen.getByTestId('workflow-session-row')
    const nameSpan = row.querySelector('span.font-mono')
    expect(nameSpan).not.toBeNull()
    expect(nameSpan!.className).toContain('truncate')
    expect(nameSpan!.className).toContain('min-w-0')

    const modelBadge = row.querySelector('span[title]')
    expect(modelBadge).not.toBeNull()
    expect(modelBadge!.className).toContain('truncate')
    expect(modelBadge!.className).toContain('min-w-0')
    expect(modelBadge!.className).toContain('ml-auto')
    // On narrow viewports the badge must be allowed to shrink below 180px (max-w-full);
    // on wider viewports the existing 180px ceiling (sm:max-w-[180px]) still applies.
    expect(modelBadge!.className).toContain('max-w-full')
    expect(modelBadge!.className).toContain('sm:max-w-[180px]')
  })

  it('keeps metric chips on a wrapping line and truncates the failure reason', () => {
    mockedUseWorkflowRunSessions.mockReturnValue({
      isLoading: false,
      sessions: [
        session({
          id: 's-build',
          sessionName: 'build',
          status: 'failed',
          usage: { totalTokens: 10_000 },
          failureReason: 'probe timed out because the runner exceeded its budget',
          createdAt: '2026-06-12T10:03:00.000Z',
        }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    const metrics = screen.getByTestId('workflow-session-row-metrics')
    expect(metrics.className).toContain('flex')
    expect(metrics.className).toContain('flex-wrap')

    const row = screen.getByTestId('workflow-session-row')
    const failure = Array.from(row.querySelectorAll('div')).find(
      (node) => node.textContent === 'probe timed out because the runner exceeded its budget',
    )
    expect(failure).toBeDefined()
    expect(failure!.className).toContain('truncate')
    expect(failure!.className).toContain('min-w-0')
  })

  it('does not declare any fixed-width class on the row or its children', () => {
    mockedUseWorkflowRunSessions.mockReturnValue({
      isLoading: false,
      sessions: [
        session({
          id: 's-plan',
          sessionName: 'plan',
          status: 'completed',
          model: 'minimax/MiniMax-M3',
          failureReason: 'short',
          createdAt: '2026-06-12T10:01:00.000Z',
        }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    const row = screen.getByTestId('workflow-session-row')
    const header = screen.getByTestId('workflow-session-row-header')
    const metrics = screen.getByTestId('workflow-session-row-metrics')

    // The model badge keeps `sm:max-w-[180px]` as an upper bound, but on narrow
    // viewports `max-w-full` lets it shrink; nothing on the row declares a fixed
    // `w-[Npx]` style that would force horizontal overflow.
    const fixedWidthPattern = /(^|\s)w-\[\d+(?:\.\d+)?px\]/
    expect(fixedWidthPattern.test(row.className)).toBe(false)
    expect(fixedWidthPattern.test(header.className)).toBe(false)
    expect(fixedWidthPattern.test(metrics.className)).toBe(false)
  })
})
