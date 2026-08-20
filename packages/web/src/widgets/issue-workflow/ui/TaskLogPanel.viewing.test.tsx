import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import { issueWorkflowTaskLogQueryOptions } from '../../../entities/issue'
import type { TaskLogPage } from '../../../entities/issue/model/task-log'
import { TaskLogPanel as DefaultTaskLogPanel, type TaskLogDataHook, type TaskLogPanelProps } from './TaskLogPanel'
import {
  emitTaskLog,
  installDownloadSpy,
  makeEnvelope,
  makeLine,
  makePage,
  mockConnectionBuilder,
  newQueryClient,
  projects,
  readBlobText,
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

describe('TaskLogPanel log viewing', () => {
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

  it('renders one chip per distinct source in lexicographic order with no absent-source chips', async () => {
    const testState = createTaskLogTestState(
      makePage([
        makeLine({ seq: 1, source: 'cleanup', text: 'rm -rf tmp' }),
        makeLine({ seq: 2, source: 'workspace-prep', text: 'cloning' }),
        makeLine({ seq: 3, source: 'action:rebase', text: 'rebasing' }),
        makeLine({ seq: 4, source: 'branch-check', text: 'on master' }),
      ]),
    )

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')
    const chipsContainer = await screen.findByTestId('task-log-source-chips')
    const chipLabels = Array.from(chipsContainer.querySelectorAll('button')).map((b) => b.textContent?.trim())
    expect(chipLabels).toEqual(['action:rebase', 'branch-check', 'cleanup', 'workspace-prep'])
  })

  it('narrows visible lines in real time as the user types a keyword (case-insensitive)', async () => {
    const testState = createTaskLogTestState(
      makePage([
        makeLine({ seq: 1, source: 'workspace-prep', text: 'Cloning repo' }),
        makeLine({ seq: 2, source: 'action:rebase', text: 'CONFLICT (content)' }),
        makeLine({ seq: 3, source: 'action:rebase', text: 'Patch failed' }),
      ]),
    )

    const user = userEvent.setup()
    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')
    await screen.findByText('Cloning repo')

    const fetchSpy = vi.spyOn(testState.queryClient, 'fetchQuery')
    const input = await screen.findByTestId('task-log-search-input')
    await user.type(input, 'CONFLICT')

    await waitFor(() => {
      expect(screen.queryByText('Cloning repo')).not.toBeInTheDocument()
    })
    expect(screen.getByText('CONFLICT (content)')).toBeInTheDocument()
    expect(screen.queryByText('Patch failed')).not.toBeInTheDocument()
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('searches source as well as text (case-insensitive)', async () => {
    const testState = createTaskLogTestState(
      makePage([
        makeLine({ seq: 1, source: 'action:rebase', text: 'starting' }),
        makeLine({ seq: 2, source: 'branch-check', text: 'on master' }),
      ]),
    )

    const user = userEvent.setup()
    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')
    await screen.findByText('starting')

    await user.type(await screen.findByTestId('task-log-search-input'), 'REBASE')

    await waitFor(() => {
      expect(screen.queryByText('on master')).not.toBeInTheDocument()
    })
    expect(screen.getByText('starting')).toBeInTheDocument()
  })

  it('toggling a source chip hides only its lines while keeping other sources visible (opt-out semantics)', async () => {
    const testState = createTaskLogTestState(
      makePage([
        makeLine({ seq: 1, source: 'action:rebase', text: 'rebasing' }),
        makeLine({ seq: 2, source: 'branch-check', text: 'on master' }),
        makeLine({ seq: 3, source: 'cleanup', text: 'rm tmp' }),
      ]),
    )

    const user = userEvent.setup()
    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')

    const chip = await screen.findByTestId('task-log-source-chip-action:rebase')
    await user.click(chip)

    await waitFor(() => {
      expect(screen.queryByText('rebasing')).not.toBeInTheDocument()
    })
    expect(screen.getByText('on master')).toBeInTheDocument()
    expect(screen.getByText('rm tmp')).toBeInTheDocument()
  })

  it('newly-arrived source from a live delta remains visible by default (opt-out set)', async () => {
    const testState = createTaskLogTestState(
      makePage([makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' })]),
    )

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')
    const chipBar = await screen.findByTestId('task-log-source-chips')
    expect(chipBar.querySelectorAll('button')).toHaveLength(1)

    await emitTaskLog(
      makeEnvelope([
        { seq: 2, source: 'cleanup', text: 'rm tmp' },
        { seq: 3, source: 'branch-check', text: 'on master' },
      ]),
    )

    await waitFor(() => {
      expect(screen.getByText('rm tmp')).toBeInTheDocument()
    })
    expect(screen.getByText('on master')).toBeInTheDocument()
    expect(chipBar.querySelectorAll('button')).toHaveLength(3)
  })

  it('composes search AND source filter so a line must pass both', async () => {
    const testState = createTaskLogTestState(
      makePage([
        makeLine({ seq: 1, source: 'action:rebase', text: 'CONFLICT (content)' }),
        makeLine({ seq: 2, source: 'action:rebase', text: 'rebasing' }),
        makeLine({ seq: 3, source: 'cleanup', text: 'CONFLICT during cleanup' }),
      ]),
    )

    const user = userEvent.setup()
    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')

    await user.click(await screen.findByTestId('task-log-source-chip-cleanup'))
    await user.type(await screen.findByTestId('task-log-search-input'), 'CONFLICT')

    await waitFor(() => {
      expect(screen.queryByText('CONFLICT during cleanup')).not.toBeInTheDocument()
    })
    expect(screen.queryByText('rebasing')).not.toBeInTheDocument()
    expect(screen.getByText('CONFLICT (content)')).toBeInTheDocument()
  })

  it('exports the currently filtered view as a .txt Blob with the convention filename (filter applied)', async () => {
    const testState = createTaskLogTestState(
      makePage([
        makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' }),
        makeLine({ seq: 2, source: 'action:rebase', text: 'rebasing-1' }),
        makeLine({ seq: 3, source: 'action:rebase', text: 'rebasing-2' }),
      ]),
    )

    const user = userEvent.setup()
    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')

    await user.click(await screen.findByTestId('task-log-source-chip-workspace-prep'))

    await waitFor(() => {
      expect(screen.queryByText('cloning')).not.toBeInTheDocument()
    })

    const capture = installDownloadSpy()
    const fetchSpy = vi.spyOn(testState.queryClient, 'fetchQuery')

    const download = await screen.findByTestId('task-log-download-button')
    await user.click(download)

    await waitFor(() => {
      expect(capture.clicks.length).toBeGreaterThan(0)
    })

    expect(capture.clicks[0].download).toMatch(/^task-logs-build-task-1-\d{4}-\d{2}-\d{2}\.txt$/)
    expect(fetchSpy).not.toHaveBeenCalled()

    const blobText = await readBlobText(capture.blob)
    const lines = blobText.split('\n')
    expect(lines).toEqual(['rebasing-1', 'rebasing-2'])
  })

  it('preserves colon-containing task ids in the download filename', async () => {
    const testState = createTaskLogTestState(makePage([makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' })]))

    const user = userEvent.setup()
    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="integrate:prepare" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')

    const capture = installDownloadSpy()
    await user.click(await screen.findByTestId('task-log-download-button'))

    await waitFor(() => {
      expect(capture.clicks.length).toBeGreaterThan(0)
    })

    expect(capture.clicks[0].download).toMatch(/^task-logs-integrate:prepare-\d{4}-\d{2}-\d{2}\.txt$/)
  })

  it('exports the full loaded log when no filter is active', async () => {
    const testState = createTaskLogTestState(
      makePage([
        makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
        makeLine({ seq: 2, source: 'action:rebase', text: 'line-2' }),
        makeLine({ seq: 3, source: 'cleanup', text: 'line-3' }),
      ]),
    )

    const user = userEvent.setup()
    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')

    const capture = installDownloadSpy()
    const download = await screen.findByTestId('task-log-download-button')
    await user.click(download)

    await waitFor(() => {
      expect(capture.clicks.length).toBeGreaterThan(0)
    })

    expect(capture.clicks[0].download).toMatch(/^task-logs-build-task-1-\d{4}-\d{2}-\d{2}\.txt$/)
    const blobText = await readBlobText(capture.blob)
    expect(blobText.split('\n')).toEqual(['line-1', 'line-2', 'line-3'])
  })

  it('disables the download button when the filtered set is empty', async () => {
    const testState = createTaskLogTestState(
      makePage([makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' })]),
    )

    const user = userEvent.setup()
    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')

    await user.click(await screen.findByTestId('task-log-source-chip-workspace-prep'))

    const download = await screen.findByTestId('task-log-download-button')
    expect(download).toBeDisabled()
  })

  it('opens with the default state: empty search input, every source chip enabled, every loaded line visible', async () => {
    const testState = createTaskLogTestState(
      makePage([
        makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
        makeLine({ seq: 2, source: 'action:rebase', text: 'line-2' }),
      ]),
    )

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')

    const input = (await screen.findByTestId('task-log-search-input')) as HTMLInputElement
    expect(input.value).toBe('')

    const chipBar = await screen.findByTestId('task-log-source-chips')
    const chips = Array.from(chipBar.querySelectorAll('button'))
    expect(chips).toHaveLength(2)
    for (const chip of chips) {
      expect(chip.getAttribute('aria-pressed')).toBe('true')
    }

    expect(screen.getByText('line-1')).toBeInTheDocument()
    expect(screen.getByText('line-2')).toBeInTheDocument()
  })

  it('renders the no-search-match boundary when search yields zero results', async () => {
    const testState = createTaskLogTestState(
      makePage([makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' })]),
    )

    const user = userEvent.setup()
    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')

    await user.type(await screen.findByTestId('task-log-search-input'), 'zzz-no-match')

    expect(await screen.findByTestId('task-log-no-search-match')).toBeInTheDocument()
    expect(screen.queryByText('cloning')).not.toBeInTheDocument()
  })

  it('renders the no-source-filter boundary when all sources are disabled and search is empty', async () => {
    const testState = createTaskLogTestState(
      makePage([
        makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' }),
        makeLine({ seq: 2, source: 'cleanup', text: 'rm tmp' }),
      ]),
    )

    const user = userEvent.setup()
    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')

    await user.click(await screen.findByTestId('task-log-source-chip-workspace-prep'))
    await user.click(await screen.findByTestId('task-log-source-chip-cleanup'))

    expect(await screen.findByTestId('task-log-no-source-match')).toBeInTheDocument()
    expect(screen.queryByTestId('task-log-no-search-match')).not.toBeInTheDocument()
  })

  it('prefers the no-search-match boundary when search and source filters both yield zero rows', async () => {
    const testState = createTaskLogTestState(
      makePage([makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' })]),
    )

    const user = userEvent.setup()
    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')

    await user.click(await screen.findByTestId('task-log-source-chip-workspace-prep'))
    await user.type(await screen.findByTestId('task-log-search-input'), 'never-matches')

    expect(await screen.findByTestId('task-log-no-search-match')).toBeInTheDocument()
    expect(screen.queryByTestId('task-log-no-source-match')).not.toBeInTheDocument()
  })

  it('preserves the loading and error boundary messages', async () => {
    _taskLogState = 'loading'
    const queryClientLoading = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const { unmount } = render(
      <QueryClientProvider client={queryClientLoading}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />
        </ProjectProvider>
      </QueryClientProvider>,
    )
    await screen.findByTestId('task-log-panel')
    expect(screen.getByText('Loading execution log…')).toBeInTheDocument()
    unmount()

    _taskLogState = 'error'
    const queryClientError = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClientError}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />
        </ProjectProvider>
      </QueryClientProvider>,
    )
    await screen.findByTestId('task-log-panel')
    expect(await screen.findByText('Execution log unavailable')).toBeInTheDocument()
  })

  it('does not force-scroll on a new line while the user is paused away from the bottom', async () => {
    const testState = createTaskLogTestState(makePage([makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' })]))

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')

    const scrollNode = (await screen.findByTestId('task-log-scroll')) as HTMLDivElement
    Object.defineProperty(scrollNode, 'scrollHeight', { configurable: true, value: 2000 })
    Object.defineProperty(scrollNode, 'clientHeight', { configurable: true, value: 200 })
    scrollNode.scrollTop = 0

    await act(async () => {
      fireEvent.scroll(scrollNode)
    })

    await emitTaskLog(makeEnvelope([{ seq: 2, source: 'workspace-prep', text: 'line-2' }]))

    await waitFor(() => {
      expect(screen.getByText('line-2')).toBeInTheDocument()
    })

    expect(scrollNode.scrollTop).toBe(0)
  })

  it('does not force-scroll when a filter change hides lines while the user is paused away from the bottom', async () => {
    const testState = createTaskLogTestState(
      makePage([
        makeLine({ seq: 1, source: 'workspace-prep', text: 'needle line' }),
        makeLine({ seq: 2, source: 'cleanup', text: 'other line' }),
      ]),
    )

    const user = userEvent.setup()
    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')

    const scrollNode = (await screen.findByTestId('task-log-scroll')) as HTMLDivElement
    Object.defineProperty(scrollNode, 'scrollHeight', { configurable: true, value: 2000 })
    Object.defineProperty(scrollNode, 'clientHeight', { configurable: true, value: 200 })
    scrollNode.scrollTop = 0

    await act(async () => {
      fireEvent.scroll(scrollNode)
    })

    await user.type(await screen.findByTestId('task-log-search-input'), 'needle')

    await waitFor(() => {
      expect(screen.queryByText('other line')).not.toBeInTheDocument()
    })
    expect(screen.getByText('needle line')).toBeInTheDocument()
    expect(scrollNode.scrollTop).toBe(0)
  })

  it('resumes auto-follow near the bottom and follows the next visible-line change', async () => {
    const testState = createTaskLogTestState(
      makePage([
        makeLine({ seq: 1, source: 'workspace-prep', text: 'alpha line' }),
        makeLine({ seq: 2, source: 'cleanup', text: 'beta line' }),
      ]),
    )

    const user = userEvent.setup()
    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')

    const scrollNode = (await screen.findByTestId('task-log-scroll')) as HTMLDivElement
    Object.defineProperty(scrollNode, 'scrollHeight', { configurable: true, value: 2000 })
    Object.defineProperty(scrollNode, 'clientHeight', { configurable: true, value: 200 })
    scrollNode.scrollTop = 0

    await act(async () => {
      fireEvent.scroll(scrollNode)
    })

    scrollNode.scrollTop = 1795
    await act(async () => {
      fireEvent.scroll(scrollNode)
    })

    Object.defineProperty(scrollNode, 'scrollHeight', { configurable: true, value: 2200 })
    await user.type(await screen.findByTestId('task-log-search-input'), 'beta')

    await waitFor(() => {
      expect(screen.queryByText('alpha line')).not.toBeInTheDocument()
    })
    expect(screen.getByText('beta line')).toBeInTheDocument()
    await waitFor(() => {
      expect(scrollNode.scrollTop).toBe(2200)
    })
  })

  it('appends live lines in sequence when no filter is active', async () => {
    const testState = createTaskLogTestState(makePage([makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' })]))

    renderWithTaskLogProviders(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      testState,
    )

    await screen.findByTestId('task-log-panel')
    await screen.findByText('line-1')

    await emitTaskLog(
      makeEnvelope([
        { seq: 2, source: 'workspace-prep', text: 'line-2' },
        { seq: 3, source: 'workspace-prep', text: 'line-3' },
      ]),
    )

    await waitFor(() => {
      expect(screen.getByText('line-3')).toBeInTheDocument()
    })

    const lines = Array.from(document.querySelectorAll('[data-testid="task-log-lines"] li')).map((li) => li.textContent)
    expect(lines[0]).toMatch(/line-1/)
    expect(lines[1]).toMatch(/line-2/)
    expect(lines[2]).toMatch(/line-3/)
  })
})
