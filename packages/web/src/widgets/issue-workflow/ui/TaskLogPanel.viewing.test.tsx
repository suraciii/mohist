// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import { getIssueWorkflowTaskLog } from '../../../entities/issue'
import { TaskLogPanel } from './TaskLogPanel'
import {
  buildHarness,
  fakeConnections,
  flushAndGetLastConnection,
  installDownloadSpy,
  makeEnvelope,
  makeLine,
  makePage,
  mockConnectionBuilder,
  projects,
  readBlobText,
  recordedInvokes,
  renderWithHarness,
} from './_taskLogPanelTestUtils'

vi.mock('@microsoft/signalr', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@microsoft/signalr')>()
  return {
    ...actual,
    HubConnectionBuilder: vi.fn(),
  }
})

vi.mock('../../../entities/issue/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue/api/client')>()
  return {
    ...actual,
    getIssueWorkflowTaskLog: vi.fn(),
  }
})

const mockedGetIssueWorkflowTaskLog = vi.mocked(getIssueWorkflowTaskLog)

describe('TaskLogPanel — viewing enhancement (Phase 3a T-001)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    fakeConnections.length = 0
    recordedInvokes.length = 0
    mockConnectionBuilder()
  })

  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
  })

  it('renders one chip per distinct source in lexicographic order with no absent-source chips', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'cleanup', text: 'rm -rf tmp' }),
      makeLine({ seq: 2, source: 'workspace-prep', text: 'cloning' }),
      makeLine({ seq: 3, source: 'action:rebase', text: 'rebasing' }),
      makeLine({ seq: 4, source: 'branch-check', text: 'on master' }),
    ]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')
    const chipsContainer = await screen.findByTestId('task-log-source-chips')
    const chipLabels = Array.from(chipsContainer.querySelectorAll('button')).map((b) => b.textContent?.trim())
    expect(chipLabels).toEqual([
      'action:rebase',
      'branch-check',
      'cleanup',
      'workspace-prep',
    ])
  })

  it('narrows visible lines in real time as the user types a keyword (case-insensitive)', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'Cloning repo' }),
      makeLine({ seq: 2, source: 'action:rebase', text: 'CONFLICT (content)' }),
      makeLine({ seq: 3, source: 'action:rebase', text: 'Patch failed' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')
    await screen.findByText('Cloning repo')

    const fetchSpy = vi.spyOn(harness.queryClient, 'fetchQuery')
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
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'action:rebase', text: 'starting' }),
      makeLine({ seq: 2, source: 'branch-check', text: 'on master' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
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
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'action:rebase', text: 'rebasing' }),
      makeLine({ seq: 2, source: 'branch-check', text: 'on master' }),
      makeLine({ seq: 3, source: 'cleanup', text: 'rm tmp' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
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
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' }),
    ]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')
    const chipBar = await screen.findByTestId('task-log-source-chips')
    expect(chipBar.querySelectorAll('button')).toHaveLength(1)

    const conn = await flushAndGetLastConnection()
    const handler = conn.handlers.get('OnTaskLogDelta')
    expect(handler).toBeDefined()

    await act(async () => {
      handler!(makeEnvelope([
        { seq: 2, source: 'cleanup', text: 'rm tmp' },
        { seq: 3, source: 'branch-check', text: 'on master' },
      ]))
    })

    await waitFor(() => {
      expect(screen.getByText('rm tmp')).toBeInTheDocument()
    })
    expect(screen.getByText('on master')).toBeInTheDocument()
    expect(chipBar.querySelectorAll('button')).toHaveLength(3)
  })

  it('composes search AND source filter so a line must pass both', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'action:rebase', text: 'CONFLICT (content)' }),
      makeLine({ seq: 2, source: 'action:rebase', text: 'rebasing' }),
      makeLine({ seq: 3, source: 'cleanup', text: 'CONFLICT during cleanup' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
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
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' }),
      makeLine({ seq: 2, source: 'action:rebase', text: 'rebasing-1' }),
      makeLine({ seq: 3, source: 'action:rebase', text: 'rebasing-2' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    await user.click(await screen.findByTestId('task-log-source-chip-workspace-prep'))

    await waitFor(() => {
      expect(screen.queryByText('cloning')).not.toBeInTheDocument()
    })

    const capture = installDownloadSpy()
    const fetchSpy = vi.spyOn(window, 'fetch')

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
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="integrate:prepare" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
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
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
      makeLine({ seq: 2, source: 'action:rebase', text: 'line-2' }),
      makeLine({ seq: 3, source: 'cleanup', text: 'line-3' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
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
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    await user.click(await screen.findByTestId('task-log-source-chip-workspace-prep'))

    const download = await screen.findByTestId('task-log-download-button')
    expect(download).toBeDisabled()
  })

  it('opens with the default state: empty search input, every source chip enabled, every loaded line visible', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
      makeLine({ seq: 2, source: 'action:rebase', text: 'line-2' }),
    ]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
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
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    await user.type(await screen.findByTestId('task-log-search-input'), 'zzz-no-match')

    expect(await screen.findByTestId('task-log-no-search-match')).toBeInTheDocument()
    expect(screen.queryByText('cloning')).not.toBeInTheDocument()
  })

  it('renders the no-source-filter boundary when all sources are disabled and search is empty', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' }),
      makeLine({ seq: 2, source: 'cleanup', text: 'rm tmp' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    await user.click(await screen.findByTestId('task-log-source-chip-workspace-prep'))
    await user.click(await screen.findByTestId('task-log-source-chip-cleanup'))

    expect(await screen.findByTestId('task-log-no-source-match')).toBeInTheDocument()
    expect(screen.queryByTestId('task-log-no-search-match')).not.toBeInTheDocument()
  })

  it('prefers the no-search-match boundary when search and source filters both yield zero rows', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'cloning' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    await user.click(await screen.findByTestId('task-log-source-chip-workspace-prep'))
    await user.type(await screen.findByTestId('task-log-search-input'), 'never-matches')

    expect(await screen.findByTestId('task-log-no-search-match')).toBeInTheDocument()
    expect(screen.queryByTestId('task-log-no-source-match')).not.toBeInTheDocument()
  })

  it('preserves the loading and error boundary messages', async () => {
    mockedGetIssueWorkflowTaskLog.mockReturnValueOnce(new Promise(() => {}))
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

    mockedGetIssueWorkflowTaskLog.mockRejectedValue(new Error('boom'))
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
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
    ]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    const scrollNode = (await screen.findByTestId('task-log-scroll')) as HTMLDivElement
    Object.defineProperty(scrollNode, 'scrollHeight', { configurable: true, value: 2000 })
    Object.defineProperty(scrollNode, 'clientHeight', { configurable: true, value: 200 })
    scrollNode.scrollTop = 0

    await act(async () => {
      fireEvent.scroll(scrollNode)
    })

    const conn = await flushAndGetLastConnection()
    const handler = conn.handlers.get('OnTaskLogDelta')
    expect(handler).toBeDefined()

    await act(async () => {
      handler!(makeEnvelope([{ seq: 2, source: 'workspace-prep', text: 'line-2' }]))
    })

    await waitFor(() => {
      expect(screen.getByText('line-2')).toBeInTheDocument()
    })

    expect(scrollNode.scrollTop).toBe(0)
  })

  it('does not force-scroll when a filter change hides lines while the user is paused away from the bottom', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'needle line' }),
      makeLine({ seq: 2, source: 'cleanup', text: 'other line' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
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
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'alpha line' }),
      makeLine({ seq: 2, source: 'cleanup', text: 'beta line' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />,
      harness,
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

  it('live-append during a running task still appends in seq order when no filter is active (non-regression)', async () => {
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'line-1' }),
    ]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="running" />,
      harness,
    )

    await screen.findByTestId('task-log-panel')
    await screen.findByText('line-1')

    const conn = await flushAndGetLastConnection()
    const handler = conn.handlers.get('OnTaskLogDelta')
    expect(handler).toBeDefined()

    await act(async () => {
      handler!(makeEnvelope([
        { seq: 2, source: 'workspace-prep', text: 'line-2' },
        { seq: 3, source: 'workspace-prep', text: 'line-3' },
      ]))
    })

    await waitFor(() => {
      expect(screen.getByText('line-3')).toBeInTheDocument()
    })

    const lines = Array.from(document.querySelectorAll('[data-testid="task-log-lines"] li')).map((li) => li.textContent)
    expect(lines[0]).toMatch(/line-1/)
    expect(lines[1]).toMatch(/line-2/)
    expect(lines[2]).toMatch(/line-3/)
  })
})
