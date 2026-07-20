import { beforeEach, describe, expect, it } from 'vitest'
import { fireEvent, screen, within } from '@testing-library/react'
import type { ReactElement } from 'react'
import { createQueryClient, render as renderWithProviders } from '../../../../tests/test-utils'
import { WorkflowSessionsPanel } from './WorkflowSessionsPanel'
import type { WorkflowRunSession } from '../../../entities/coder-session'

let sessionsData: WorkflowRunSession[] = []

function setWorkflowRunSessions(value: { isLoading: boolean; sessions: WorkflowRunSession[] }) {
  sessionsData = value.sessions
}

function render(ui: ReactElement) {
  const queryClient = createQueryClient()
  queryClient.setQueryData(['workflow-runs', 'workflow-run-1', 'sessions'], sessionsData)
  return renderWithProviders(ui, { queryClient })
}

beforeEach(() => {
  sessionsData = []
})

function session(overrides: Partial<WorkflowRunSession> & { usage?: Partial<NonNullable<WorkflowRunSession['usage']>>; eventSummary?: Partial<NonNullable<WorkflowRunSession['eventSummary']>> }): WorkflowRunSession {
  const { usage: usageOverride, eventSummary: eventSummaryOverride, ...rest } = overrides
  return {
    id: rest.id ?? 'session-1',
    workflowRunId: 'workflow-run-1',
    sessionName: rest.sessionName ?? 'check',
    runtimeSessionId: rest.runtimeSessionId ?? 'runtime-1',
    projectId: 'project-1',
    issueNumber: 55,
    runnerId: 'runner-1',
    status: rest.status ?? 'completed',
    stage: rest.stage ?? 'check',
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
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        session({
          id: 's-check',
          sessionName: 'review-repair',
          stage: 'check',
          status: 'active',
          usage: {
            totalTokens: 588_371,
            costAmount: 0,
            costCurrency: 'USD',
            contextWindowUsed: 252_565,
            contextWindowSize: 512_000,
            contextUsagePercent: 49.3,
          },
          createdAt: '2026-06-12T10:02:00.000Z',
        }),
        session({
          id: 's-plan',
          sessionName: 'proposal-draft',
          stage: 'plan',
          status: 'completed',
          model: 'configured/PlanModel',
          eventSummary: { resolvedModel: 'resolved/PlanModel' },
          usage: {
            totalTokens: 42_000,
            contextWindowUsed: 32_000,
            contextWindowSize: 200_000,
            contextUsagePercent: 16,
          },
          createdAt: '2026-06-12T10:01:00.000Z',
        }),
        session({
          id: 's-build',
          sessionName: 'compile-assets',
          stage: 'build',
          status: 'failed',
          usage: { totalTokens: 10_000 },
          failureReason: 'probe timed out',
          createdAt: '2026-06-12T10:03:00.000Z',
        }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    expect(screen.getByText('Sessions')).toBeInTheDocument()
    expect(screen.getByText(/3 sessions/)).toBeInTheDocument()
    expect(screen.getByText(/640\.4k processed/)).toBeInTheDocument()
    expect(screen.getByText(/peak 49% review-repair/)).toBeInTheDocument()
    expect(screen.getByText('proposal-draft')).toBeInTheDocument()
    expect(screen.getByText('review-repair')).toBeInTheDocument()
    expect(screen.getByText('compile-assets')).toBeInTheDocument()
    expect(screen.getAllByText('minimax/MiniMax-M3')).toHaveLength(2)
    expect(screen.getByText('configured/PlanModel -> resolved/PlanModel')).toBeInTheDocument()
    expect(screen.getByText('probe timed out')).toBeInTheDocument()
  })

  it('does not render without a workflow run id', () => {
    setWorkflowRunSessions({ isLoading: false, sessions: [] })

    const { container } = render(<WorkflowSessionsPanel issueNumber={55} workflowRunId={null} />)

    expect(container).toBeEmptyDOMElement()
  })

  it('renders a compact filter/sort control row above the session list', () => {
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        session({ id: 's-plan', sessionName: 'proposal-draft', stage: 'plan', status: 'completed', createdAt: '2026-06-12T10:01:00.000Z' }),
        session({ id: 's-build', sessionName: 'compile-assets', stage: 'build', status: 'failed', createdAt: '2026-06-12T10:03:00.000Z' }),
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
    expect(Array.from(stageFilter.querySelectorAll('option')).map((o) => o.disabled)).toEqual([false, false, false, false, false])
  })

  it('filtering by status hides non-matching sessions and surfaces a notice', () => {
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        session({ id: 's-plan', sessionName: 'proposal-draft', stage: 'plan', status: 'completed', createdAt: '2026-06-12T10:01:00.000Z' }),
        session({ id: 's-build', sessionName: 'compile-assets', stage: 'build', status: 'failed', createdAt: '2026-06-12T10:03:00.000Z' }),
        session({ id: 's-check', sessionName: 'review-repair', stage: 'check', status: 'running', createdAt: '2026-06-12T10:02:00.000Z' }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    const statusFilter = screen.getByTestId('workflow-sessions-status-filter') as HTMLSelectElement
    fireEvent.change(statusFilter, { target: { value: 'failed' } })

    expect(screen.queryByText('proposal-draft')).not.toBeInTheDocument()
    expect(screen.queryByText('review-repair')).not.toBeInTheDocument()
    expect(screen.getByText('compile-assets')).toBeInTheDocument()
    expect(screen.getByTestId('workflow-sessions-filter-notice')).toHaveTextContent('Showing 1 of 3 sessions')
  })

  it('filtering by stage hides non-matching sessions', () => {
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        session({ id: 's-plan', sessionName: 'proposal-draft', stage: 'plan', status: 'completed', createdAt: '2026-06-12T10:01:00.000Z' }),
        session({ id: 's-build', sessionName: 'compile-assets', stage: 'build', status: 'completed', createdAt: '2026-06-12T10:03:00.000Z' }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    const stageFilter = screen.getByTestId('workflow-sessions-stage-filter') as HTMLSelectElement
    fireEvent.change(stageFilter, { target: { value: 'build' } })

    expect(screen.queryByText('proposal-draft')).not.toBeInTheDocument()
    expect(screen.getByText('compile-assets')).toBeInTheDocument()
  })

  it('sorting by tokens reorders visible sessions', () => {
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        session({
          id: 's-plan',
          sessionName: 'proposal-draft',
          stage: 'plan',
          status: 'completed',
          createdAt: '2026-06-12T10:00:00.000Z',
          usage: { totalTokens: 1_000 },
        }),
        session({
          id: 's-build',
          sessionName: 'compile-assets',
          stage: 'build',
          status: 'completed',
          createdAt: '2026-06-12T10:01:00.000Z',
          usage: { totalTokens: 5_000 },
        }),
        session({
          id: 's-check',
          sessionName: 'review-repair',
          stage: 'check',
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
      '/Test%20Project/issues/55/workflow/sessions/compile-assets',
      '/Test%20Project/issues/55/workflow/sessions/review-repair',
      '/Test%20Project/issues/55/workflow/sessions/proposal-draft',
    ])
  })

  it('shows an empty-result message when filters hide every session', () => {
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        session({ id: 's-plan', sessionName: 'proposal-draft', stage: 'plan', status: 'completed' }),
        session({ id: 's-build', sessionName: 'compile-assets', stage: 'build', status: 'completed' }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    const stageFilter = screen.getByTestId('workflow-sessions-stage-filter') as HTMLSelectElement
    fireEvent.change(stageFilter, { target: { value: 'check' } })

    expect(screen.getByText(/No sessions match the current filters/)).toBeInTheDocument()
  })

  it('lets a user select an absent executable stage and shows an empty result', () => {
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        session({ id: 's-plan', sessionName: 'proposal-draft', stage: 'plan', status: 'completed' }),
        session({ id: 's-build', sessionName: 'compile-assets', stage: 'build', status: 'completed' }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    const stageFilter = screen.getByTestId('workflow-sessions-stage-filter') as HTMLSelectElement
    const checkOption = within(stageFilter).getByRole('option', { name: 'Check' }) as HTMLOptionElement
    expect(checkOption.disabled).toBe(false)

    fireEvent.change(stageFilter, { target: { value: 'check' } })

    expect(stageFilter.value).toBe('check')
    expect(screen.getByText(/No sessions match the current filters/)).toBeInTheDocument()
  })
})

describe('issue-level usage aggregation', () => {
  it('sums total tokens and groups cost by currency across multiple sessions', () => {
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        session({
          id: 's-plan', sessionName: 'plan', stage: 'plan', status: 'completed',
          usage: { totalTokens: 100_000, costAmount: 0.05, costCurrency: 'USD' },
          createdAt: '2026-06-12T10:00:00.000Z',
        }),
        session({
          id: 's-build', sessionName: 'build', stage: 'build', status: 'completed',
          usage: { totalTokens: 200_000, costAmount: 0.12, costCurrency: 'USD' },
          createdAt: '2026-06-12T10:01:00.000Z',
        }),
        session({
          id: 's-check', sessionName: 'check', stage: 'check', status: 'completed',
          usage: { totalTokens: 50_000, costAmount: 0.03, costCurrency: 'EUR' },
          createdAt: '2026-06-12T10:02:00.000Z',
        }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    expect(screen.getByText(/3 sessions/)).toBeInTheDocument()
    expect(screen.getByText(/350\.0k processed/)).toBeInTheDocument()
    const headerEl = screen.getByText(/3 sessions/)
    expect(headerEl.textContent).toContain('$0.17')
    expect(headerEl.textContent).toContain('€0.03')
  })

  it('omits aggregate totals when no session has usage data', () => {
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        session({ id: 's-plan', sessionName: 'plan', stage: 'plan', status: 'completed', createdAt: '2026-06-12T10:00:00.000Z', usage: undefined }),
        session({ id: 's-build', sessionName: 'build', stage: 'build', status: 'completed', createdAt: '2026-06-12T10:01:00.000Z', usage: undefined }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    const header = screen.getByText(/2 sessions/)
    expect(header).toBeInTheDocument()
    expect(header.textContent).toBe('2 sessions')
    expect(screen.queryByText(/processed/)).not.toBeInTheDocument()
    expect(screen.queryByText(/\$/)).not.toBeInTheDocument()
    expect(screen.queryByText(/€/)).not.toBeInTheDocument()
  })

  it('excludes non-additive fields from the aggregate total', () => {
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        session({
          id: 's-plan', sessionName: 'plan', stage: 'plan', status: 'completed',
          usage: {
            totalTokens: 100_000,
            costAmount: 0.05, costCurrency: 'USD',
            cachedReadTokens: 30_000,
            thoughtTokens: 10_000,
            contextWindowUsed: 50_000, contextWindowSize: 200_000,
          },
          createdAt: '2026-06-12T10:00:00.000Z',
        }),
        session({
          id: 's-build', sessionName: 'build', stage: 'build', status: 'completed',
          usage: {
            totalTokens: 200_000,
            costAmount: 0.12, costCurrency: 'USD',
            cachedReadTokens: 60_000,
            thoughtTokens: 20_000,
            contextWindowUsed: 100_000, contextWindowSize: 200_000,
          },
          createdAt: '2026-06-12T10:01:00.000Z',
        }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    expect(screen.getByText(/300\.0k processed/)).toBeInTheDocument()
    expect(screen.getByText(/\$0\.17/)).toBeInTheDocument()
    expect(screen.queryByText(/90k cached/)).not.toBeInTheDocument()
    expect(screen.queryByText(/30k thought/)).not.toBeInTheDocument()
  })
})

describe('WorkflowSessionRow responsive layout', () => {
  it('applies min-w-0 to the row link and header so content can shrink', () => {
    setWorkflowRunSessions({
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
    setWorkflowRunSessions({
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
    setWorkflowRunSessions({
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
    setWorkflowRunSessions({
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
