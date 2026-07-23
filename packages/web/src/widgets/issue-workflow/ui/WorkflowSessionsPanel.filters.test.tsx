import { beforeEach, describe, expect, it } from 'vitest'
import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
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

function session(overrides: Partial<WorkflowRunSession>): WorkflowRunSession {
  return {
    id: overrides.id ?? 'session-1',
    workflowRunId: 'workflow-run-1',
    sessionName: overrides.sessionName ?? 'check',
    runtimeSessionId: overrides.runtimeSessionId ?? 'runtime-1',
    projectId: 'project-1',
    issueNumber: 55,
    runnerId: 'runner-1',
    // Issue 484: filtering is now by `activity` (idle/active/unknown). `status`
    // is retained on the wire but no longer drives filtering or badges.
    activity: overrides.activity ?? 'idle',
    stage: overrides.stage ?? 'check',
    model: overrides.model ?? 'minimax/MiniMax-M3',
    workDir: null,
    processPid: null,
    createdAt: overrides.createdAt ?? '2026-06-12T10:00:00.000Z',
    startedAt: null,
    completedAt: overrides.completedAt ?? null,
    lastDataAt: overrides.lastDataAt ?? '2026-06-12T10:05:00.000Z',
    failureReason: overrides.failureReason ?? null,
    exitCode: null,
    usage: overrides.usage,
    eventSummary: overrides.eventSummary,
  }
}

describe('WorkflowSessionsPanel filters', () => {
  it('renders shared filter controls with all options', async () => {
    const user = userEvent.setup()
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        session({ id: 's-plan', sessionName: 'proposal-draft', stage: 'plan', status: 'completed', createdAt: '2026-06-12T10:01:00.000Z' }),
        session({ id: 's-build', sessionName: 'compile-assets', stage: 'build', status: 'failed', createdAt: '2026-06-12T10:03:00.000Z' }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)

    expect(screen.getByTestId('workflow-sessions-controls')).toBeInTheDocument()
    const statusFilter = screen.getByTestId('workflow-sessions-status-filter')
    const stageFilter = screen.getByTestId('workflow-sessions-stage-filter')
    const sortSelect = screen.getByTestId('workflow-sessions-sort')

    expect(within(statusFilter).getByText('All statuses')).toBeInTheDocument()
    expect(within(stageFilter).getByText('All stages')).toBeInTheDocument()
    expect(within(sortSelect).getByText('Created')).toBeInTheDocument()
    expect(statusFilter.tagName).not.toBe('SELECT')
    expect(stageFilter.tagName).not.toBe('SELECT')
    expect(sortSelect.tagName).not.toBe('SELECT')

    await user.click(stageFilter)
    const stageOptions = await screen.findAllByRole('option')
    expect(stageOptions.map((o) => o.textContent)).toEqual(['All stages', 'Plan', 'Build', 'Check', 'Integrate'])
    expect(stageOptions.map((o) => o.getAttribute('data-disabled'))).toEqual([null, null, null, null, null])
  })

  it('filters by activity and surfaces a notice', async () => {
    const user = userEvent.setup()
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        // Issue 484: status values map onto activities — a finished plan/build
        // session is `idle`, a live session is `active`, and an unconfirmable
        // failure surfaces as `unknown`.
        session({ id: 's-plan', sessionName: 'proposal-draft', stage: 'plan', activity: 'idle', createdAt: '2026-06-12T10:01:00.000Z' }),
        session({ id: 's-build', sessionName: 'compile-assets', stage: 'build', activity: 'unknown', createdAt: '2026-06-12T10:03:00.000Z' }),
        session({ id: 's-check', sessionName: 'review-repair', stage: 'check', activity: 'active', createdAt: '2026-06-12T10:02:00.000Z' }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)
    const statusFilter = screen.getByTestId('workflow-sessions-status-filter')
    await user.click(statusFilter)
    await user.click(await screen.findByRole('option', { name: 'unknown' }))

    expect(within(statusFilter).getByText('unknown')).toBeInTheDocument()
    expect(screen.queryByText('proposal-draft')).not.toBeInTheDocument()
    expect(screen.queryByText('review-repair')).not.toBeInTheDocument()
    expect(screen.getByText('compile-assets')).toBeInTheDocument()
    expect(screen.getByTestId('workflow-sessions-filter-notice')).toHaveTextContent('Showing 1 of 3 sessions')
  })

  it('filters by stage', async () => {
    const user = userEvent.setup()
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        session({ id: 's-plan', sessionName: 'proposal-draft', stage: 'plan', status: 'completed', createdAt: '2026-06-12T10:01:00.000Z' }),
        session({ id: 's-build', sessionName: 'compile-assets', stage: 'build', status: 'completed', createdAt: '2026-06-12T10:03:00.000Z' }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)
    const stageFilter = screen.getByTestId('workflow-sessions-stage-filter')
    await user.click(stageFilter)
    await user.click(await screen.findByRole('option', { name: 'Build' }))

    expect(within(stageFilter).getByText('Build')).toBeInTheDocument()
    expect(screen.queryByText('proposal-draft')).not.toBeInTheDocument()
    expect(screen.getByText('compile-assets')).toBeInTheDocument()
  })

  it('sorts by tokens', async () => {
    const user = userEvent.setup()
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        session({ id: 's-plan', sessionName: 'proposal-draft', stage: 'plan', usage: { totalTokens: 1_000 }, createdAt: '2026-06-12T10:00:00.000Z' }),
        session({ id: 's-build', sessionName: 'compile-assets', stage: 'build', usage: { totalTokens: 5_000 }, createdAt: '2026-06-12T10:01:00.000Z' }),
        session({ id: 's-check', sessionName: 'review-repair', stage: 'check', usage: { totalTokens: 2_500 }, createdAt: '2026-06-12T10:02:00.000Z' }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)
    const sortSelect = screen.getByTestId('workflow-sessions-sort')
    await user.click(sortSelect)
    await user.click(await screen.findByRole('option', { name: 'Tokens' }))

    expect(screen.getAllByRole('link').map((link) => link.getAttribute('href'))).toEqual([
      '/Test%20Project/issues/55/workflow/sessions/compile-assets',
      '/Test%20Project/issues/55/workflow/sessions/review-repair',
      '/Test%20Project/issues/55/workflow/sessions/proposal-draft',
    ])
  })

  it('shows an empty result when filters hide every session', async () => {
    const user = userEvent.setup()
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        session({ id: 's-plan', sessionName: 'proposal-draft', stage: 'plan' }),
        session({ id: 's-build', sessionName: 'compile-assets', stage: 'build' }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)
    const stageFilter = screen.getByTestId('workflow-sessions-stage-filter')
    await user.click(stageFilter)
    await user.click(await screen.findByRole('option', { name: 'Check' }))

    expect(screen.getByText(/No sessions match the current filters/)).toBeInTheDocument()
  })

  it('allows an absent executable stage', async () => {
    const user = userEvent.setup()
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        session({ id: 's-plan', sessionName: 'proposal-draft', stage: 'plan' }),
        session({ id: 's-build', sessionName: 'compile-assets', stage: 'build' }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)
    const stageFilter = screen.getByTestId('workflow-sessions-stage-filter')
    await user.click(stageFilter)
    const checkOption = await screen.findByRole('option', { name: 'Check' })
    expect(checkOption.getAttribute('data-disabled')).toBeNull()
    await user.click(checkOption)
    expect(within(stageFilter).getByText('Check')).toBeInTheDocument()
    expect(screen.getByText(/No sessions match the current filters/)).toBeInTheDocument()
  })

  it('clears a filter by selecting the "All ..." option', async () => {
    const user = userEvent.setup()
    setWorkflowRunSessions({
      isLoading: false,
      sessions: [
        // Issue 484: mix activities so selecting one narrows the list.
        session({ id: 's-plan', sessionName: 'proposal-draft', stage: 'plan', activity: 'idle', createdAt: '2026-06-12T10:01:00.000Z' }),
        session({ id: 's-build', sessionName: 'compile-assets', stage: 'build', activity: 'unknown', createdAt: '2026-06-12T10:03:00.000Z' }),
      ],
    })

    render(<WorkflowSessionsPanel issueNumber={55} workflowRunId="workflow-run-1" />)
    const renderedStatusFilter = screen.getByTestId('workflow-sessions-status-filter')
    await user.click(renderedStatusFilter)
    await user.click(await screen.findByRole('option', { name: 'unknown' }))
    expect(within(renderedStatusFilter).getByText('unknown')).toBeInTheDocument()
    expect(screen.getByTestId('workflow-sessions-filter-notice')).toHaveTextContent('Showing 1 of 2 sessions')

    await user.click(renderedStatusFilter)
    await user.click(await screen.findByRole('option', { name: 'All statuses' }))
    expect(within(renderedStatusFilter).getByText('All statuses')).toBeInTheDocument()
    expect(screen.queryByTestId('workflow-sessions-filter-notice')).not.toBeInTheDocument()
    expect(screen.getByText('proposal-draft')).toBeInTheDocument()
    expect(screen.getByText('compile-assets')).toBeInTheDocument()
  })
})
