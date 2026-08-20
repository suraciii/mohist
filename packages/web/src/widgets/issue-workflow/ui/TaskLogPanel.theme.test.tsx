import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import { issueWorkflowTaskLogQueryOptions } from '../../../entities/issue'
import type { TaskLogPage } from '../../../entities/issue/model/task-log'
import { TaskLogPanel as DefaultTaskLogPanel, type TaskLogDataHook, type TaskLogPanelProps } from './TaskLogPanel'
import {
  makeLine,
  makePage,
  mockConnectionBuilder,
  newQueryClient,
  projects,
  renderWithTaskLogProviders,
  type TaskLogTestState,
} from './_taskLogPanelTestUtils'

const _taskLogPageRef: { current: TaskLogPage | undefined } = { current: undefined }
let _taskLogState: 'ready' | 'loading' | 'error' = 'ready'

const taskLogHook: TaskLogDataHook = ({ issueNumber, taskId, projectId, workflowRunId }) =>
  useQuery({
    ...issueWorkflowTaskLogQueryOptions(projectId, issueNumber, taskId, { limit: 5000 }, true, workflowRunId),
    queryFn: async () => {
      if (_taskLogState === 'loading') return new Promise<TaskLogPage>(() => {})
      if (_taskLogState === 'error') throw new Error('boom')
      return _taskLogPageRef.current ?? makePage([])
    },
  })

function TaskLogPanel(props: Omit<TaskLogPanelProps, 'taskLogHook'>) {
  return <DefaultTaskLogPanel {...props} taskLogHook={taskLogHook} />
}

function createTaskLogTestState(initialPage: TaskLogPage | undefined): TaskLogTestState {
  const queryClient = newQueryClient()
  _taskLogPageRef.current = initialPage
  return {
    queryClient,
    page: _taskLogPageRef,
    setPage(next) {
      _taskLogPageRef.current = next
    },
  }
}

describe('TaskLogPanel theme tokens', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _taskLogPageRef.current = undefined
    _taskLogState = 'ready'
    mockConnectionBuilder()
  })

  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
  })

  it('tokenizes the panel chrome (outer surface, header label, search control, download button, source chips) to semantic theme tokens', async () => {
    const testState = createTaskLogTestState(
      makePage(
        [
          makeLine({ seq: 1, source: 'cleanup', text: 'rm -rf tmp' }),
          makeLine({ seq: 2, source: 'workspace-prep', text: 'cloning' }),
        ],
        true,
      ),
    )

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    const panel = await screen.findByTestId('task-log-panel')
    expect(panel).toHaveClass('border-border', 'bg-card')
    expect(panel).not.toHaveClass('bg-white', 'border-slate-200')

    const searchInput = await screen.findByTestId('task-log-search-input')
    expect(searchInput).toHaveClass(
      'border-input',
      'bg-background',
      'text-foreground',
      'focus:border-info',
      'focus:ring-info',
    )
    expect(searchInput).not.toHaveClass('bg-white', 'text-slate-900', 'focus:border-sky-500')

    const downloadButton = await screen.findByTestId('task-log-download-button')
    expect(downloadButton).toHaveClass('border-input', 'bg-background', 'text-foreground', 'hover:bg-muted')
    expect(downloadButton).not.toHaveClass('bg-white', 'text-slate-700', 'hover:bg-slate-50')

    const chipBar = await screen.findByTestId('task-log-source-chips')
    const chips = Array.from(chipBar.querySelectorAll('button'))
    expect(chips.length).toBeGreaterThan(0)
    for (const chip of chips) {
      expect(chip).toHaveClass('border-input', 'bg-background', 'text-foreground')
      expect(chip).not.toHaveClass('bg-slate-100', 'text-slate-700', 'border-slate-300')
    }

    const truncationBadge = await screen.findByTestId('task-log-truncation-indicator')
    expect(truncationBadge).toHaveClass('bg-warning-subtle', 'text-warning')
    expect(truncationBadge).not.toHaveClass('bg-amber-100', 'text-amber-800')
  })

  it('preserves the deliberate dark log-console surface (bg-slate-900 text-slate-100 and the foreground line colors)', async () => {
    const testState = createTaskLogTestState(
      makePage([makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning repo' })]),
    )

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    const scroll = await screen.findByTestId('task-log-scroll')
    expect(scroll).toHaveClass('bg-slate-900', 'text-slate-100')
    expect(scroll).not.toHaveClass('bg-background', 'text-foreground')

    const lines = await screen.findByTestId('task-log-lines')
    const sourceBracket = lines.querySelector('span.text-sky-300')
    expect(sourceBracket).not.toBeNull()
    expect(sourceBracket?.textContent).toBe('[workspace-prep]')

    const timestamps = lines.querySelectorAll('span.text-slate-500')
    expect(timestamps.length).toBeGreaterThan(0)
  })

  it('uses muted tokens for the disabled source chip variant', async () => {
    const testState = createTaskLogTestState(makePage([makeLine({ seq: 1, source: 'cleanup', text: 'rm -rf tmp' })]))

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    const chip = await screen.findByTestId('task-log-source-chip-cleanup')
    await act(async () => {
      chip.click()
    })

    const updatedChip = screen.getByTestId('task-log-source-chip-cleanup')
    expect(updatedChip).toHaveClass('border-border', 'bg-muted', 'text-muted-foreground', 'line-through')
    expect(updatedChip).not.toHaveClass('border-slate-200', 'bg-white', 'text-slate-400')
  })

  it('renders the loading boundary inside the preserved dark console surface', async () => {
    _taskLogState = 'loading'
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />
        </ProjectProvider>
      </QueryClientProvider>,
    )
    await screen.findByTestId('task-log-panel')
    const scroll = await screen.findByTestId('task-log-scroll')
    expect(scroll).toHaveClass('bg-slate-900', 'text-slate-100')
    expect(await screen.findByText('Loading execution log…')).toBeInTheDocument()
  })
})
