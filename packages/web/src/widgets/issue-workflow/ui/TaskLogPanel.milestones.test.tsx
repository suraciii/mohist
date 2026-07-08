// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { getIssueWorkflowTaskLog } from '../../../entities/issue'
import { getWorkflowRunSessions } from '../../../entities/coder-session/api/client'
import type { WorkflowRunSession } from '../../../entities/coder-session/model/types'
import { TaskLogPanel } from './TaskLogPanel'
import {
  buildHarness,
  fakeConnections,
  installDownloadSpy,
  makeLine,
  makePage,
  mockConnectionBuilder,
  readBlobText,
  recordedInvokes,
  renderWithHarness,
  sessionFixture,
} from './_taskLogPanelTestUtils'

vi.mock('../../../entities/issue/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue/api/client')>()
  return {
    ...actual,
    getIssueWorkflowTaskLog: vi.fn(),
  }
})

const mockedGetIssueWorkflowTaskLog = vi.mocked(getIssueWorkflowTaskLog)

const sessionEventHandlers = new Map<string, ((detail: unknown) => void)[]>()

vi.mock('../../../entities/agent/@x/events', () => ({
  onAgentEvent: vi.fn((name: string, handler: (detail: unknown) => void) => {
    if (!sessionEventHandlers.has(name)) sessionEventHandlers.set(name, [])
    sessionEventHandlers.get(name)!.push(handler)
    return () => {
      const handlers = sessionEventHandlers.get(name)
      if (handlers) {
        const idx = handlers.indexOf(handler)
        if (idx !== -1) handlers.splice(idx, 1)
      }
    }
  }),
}))

vi.mock('../../../entities/coder-session/api/client', () => ({
  getWorkflowRunSessions: vi.fn(),
}))

const mockedGetWorkflowRunSessions = vi.mocked(getWorkflowRunSessions)

describe('TaskLogPanel — agent-task milestone rows (Phase 3b T-001)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    fakeConnections.length = 0
    recordedInvokes.length = 0
    sessionEventHandlers.clear()
    mockConnectionBuilder()
  })

  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
  })

  const agentOrigin = { uses: 'mohist/acp-agent' }

  it('renders milestone rows interleaved by ISO timestamp alongside ops lines for an agent task', async () => {
    mockedGetWorkflowRunSessions.mockResolvedValue([
      sessionFixture({
        id: 'session-1',
        sessionName: 'plan-issue-339',
        startedAt: '2026-07-03T08:01:00.000Z',
        completedAt: '2026-07-03T08:04:00.000Z',
        status: 'completed',
        eventSummary: { resolvedModel: 'minimax/MiniMax-M3' },
      }),
    ])
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, timestamp: '2026-07-03T08:00:01.000Z', source: 'workspace-prep', text: 'ops-08:00:01' }),
      makeLine({ seq: 2, timestamp: '2026-07-03T08:05:00.000Z', source: 'cleanup', text: 'ops-08:05:00' }),
    ]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="completed"
        sessionName="plan-issue-339"
        origin={agentOrigin}
        classification="UserFacing"
      />,
      harness,
    )

    await screen.findByTestId('task-log-panel')
    expect(await screen.findByTestId('task-log-milestone-model-bound')).toBeInTheDocument()
    expect(await screen.findByTestId('task-log-milestone-session-ended')).toBeInTheDocument()

    const items = Array.from(document.querySelectorAll('[data-testid="task-log-lines"] > li')).map((li) => li.textContent ?? '')
    expect(items).toHaveLength(4)
    const indices = {
      ops1: items.findIndex((t) => t.includes('ops-08:00:01')),
      model: items.findIndex((t) => t.includes('Model bound')),
      ended: items.findIndex((t) => t.includes('Session ended')),
      ops2: items.findIndex((t) => t.includes('ops-08:05:00')),
    }
    expect(indices.ops1).toBeLessThan(indices.model)
    expect(indices.model).toBeLessThan(indices.ended)
    expect(indices.ended).toBeLessThan(indices.ops2)
  })

  it('renders milestones as the timeline content (suppresses the empty state) when there are no ops lines', async () => {
    mockedGetWorkflowRunSessions.mockResolvedValue([
      sessionFixture({
        id: 'session-1',
        sessionName: 'plan-issue-339',
        startedAt: '2026-07-03T08:00:01.000Z',
        completedAt: '2026-07-03T08:01:00.000Z',
        status: 'completed',
        eventSummary: { resolvedModel: 'mohist/coder-agent' },
      }),
    ])
    const harness = buildHarness(makePage([]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="completed"
        sessionName="plan-issue-339"
        origin={agentOrigin}
        classification="UserFacing"
      />,
      harness,
    )

    expect(screen.queryByTestId('task-log-empty')).not.toBeInTheDocument()
    expect(await screen.findByTestId('task-log-milestone-model-bound')).toBeInTheDocument()
    expect(await screen.findByTestId('task-log-milestone-session-ended')).toBeInTheDocument()
  })

  it('keeps showing a loading state instead of the true-empty copy while agent session summaries load', async () => {
    mockedGetWorkflowRunSessions.mockImplementation(() => new Promise<WorkflowRunSession[]>(() => {}))
    const harness = buildHarness(makePage([]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="completed"
        sessionName="plan-issue-339"
        origin={agentOrigin}
        classification="UserFacing"
      />,
      harness,
    )

    await waitFor(() => {
      expect(mockedGetWorkflowRunSessions).toHaveBeenCalledWith('wr-1')
    })
    expect(screen.getByText('Loading execution log…')).toBeInTheDocument()
    expect(screen.queryByTestId('task-log-empty')).not.toBeInTheDocument()
  })

  it('renders terminal-state milestones from the persisted summary without any real-time event for a finished agent task', async () => {
    mockedGetWorkflowRunSessions.mockResolvedValue([
      sessionFixture({
        id: 'session-1',
        sessionName: 'plan-issue-339',
        startedAt: '2026-07-03T08:00:01.000Z',
        completedAt: '2026-07-03T08:01:00.000Z',
        status: 'completed',
        eventSummary: { resolvedModel: 'minimax/MiniMax-M3' },
      }),
    ])
    const harness = buildHarness(makePage([]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="completed"
        sessionName="plan-issue-339"
        origin={agentOrigin}
        classification="UserFacing"
      />,
      harness,
    )

    expect(await screen.findByTestId('task-log-milestone-model-bound')).toBeInTheDocument()
    expect(await screen.findByTestId('task-log-milestone-session-ended')).toBeInTheDocument()
    expect(screen.getByText('minimax/MiniMax-M3')).toBeInTheDocument()
    expect(screen.getAllByText('completed').length).toBeGreaterThan(0)
  })

  it('renders failure milestones with the failureReason and applies the failed styling', async () => {
    mockedGetWorkflowRunSessions.mockResolvedValue([
      sessionFixture({
        id: 'session-1',
        sessionName: 'plan-issue-339',
        status: 'failed',
        completedAt: '2026-07-03T08:01:00.000Z',
        failureReason: 'agent stream blew up',
        eventSummary: { resolvedModel: 'minimax/MiniMax-M3' },
      }),
    ])
    const harness = buildHarness(makePage([]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="failed"
        sessionName="plan-issue-339"
        origin={agentOrigin}
        classification="UserFacing"
      />,
      harness,
    )

    expect(await screen.findByTestId('task-log-milestone-session-ended')).toBeInTheDocument()
    expect(screen.getByText(/failed/)).toBeInTheDocument()
    expect(screen.getByText(/agent stream blew up/)).toBeInTheDocument()
  })

  it('renders NO milestone rows for a pure ops task even when sessionName is present', async () => {
    mockedGetWorkflowRunSessions.mockResolvedValue([
      sessionFixture({ id: 'session-1', sessionName: 'rebase-1' }),
    ])
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'action:rebase', text: 'rebasing' }),
    ]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="failed"
        sessionName="rebase-1"
        origin={{ uses: 'mohist/rebase' }}
        classification="Orchestration"
      />,
      harness,
    )

    await screen.findByText('rebasing')
    expect(screen.queryByTestId('task-log-milestone-model-bound')).not.toBeInTheDocument()
    expect(screen.queryByTestId('task-log-milestone-session-ended')).not.toBeInTheDocument()
  })

  it('renders NO milestone rows when origin.uses is the agent action but sessionName is empty', async () => {
    mockedGetWorkflowRunSessions.mockResolvedValue([
      sessionFixture({ id: 'session-1', sessionName: '' }),
    ])
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'prep' }),
    ]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="completed"
        sessionName=""
        origin={agentOrigin}
        classification="UserFacing"
      />,
      harness,
    )

    await screen.findByText('prep')
    expect(screen.queryByTestId('task-log-milestone-model-bound')).not.toBeInTheDocument()
    expect(screen.queryByTestId('task-log-milestone-session-ended')).not.toBeInTheDocument()
  })

  it('renders milestone rows when origin.uses and sessionName are present even if classification is missing', async () => {
    mockedGetWorkflowRunSessions.mockResolvedValue([
      sessionFixture({ id: 'session-1', sessionName: 'plan-issue-339' }),
    ])
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'prep-without-classification' }),
    ]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="completed"
        sessionName="plan-issue-339"
        origin={agentOrigin}
      />,
      harness,
    )

    await screen.findByText('prep-without-classification')
    expect(await screen.findByTestId('task-log-milestone-model-bound')).toBeInTheDocument()
    expect(await screen.findByTestId('task-log-milestone-session-ended')).toBeInTheDocument()
  })

  it('renders NO milestone rows when the workflow-run sessions data is empty (graceful degradation)', async () => {
    mockedGetWorkflowRunSessions.mockResolvedValue([])
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'ops-line' }),
    ]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="completed"
        sessionName="plan-issue-339"
        origin={agentOrigin}
        classification="UserFacing"
      />,
      harness,
    )

    await screen.findByText('ops-line')
    expect(screen.queryByTestId('task-log-milestone-model-bound')).not.toBeInTheDocument()
    expect(screen.queryByTestId('task-log-milestone-session-ended')).not.toBeInTheDocument()
  })

  it('sorts the mixed timeline by timestamp when ops timestamps disagree and uses the same order for export', async () => {
    mockedGetWorkflowRunSessions.mockResolvedValue([
      sessionFixture({
        id: 'session-1',
        sessionName: 'plan-issue-339',
        startedAt: '2026-07-03T08:04:00.000Z',
        completedAt: '2026-07-03T08:06:00.000Z',
        status: 'completed',
        eventSummary: { resolvedModel: 'minimax/MiniMax-M3' },
      }),
    ])
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, timestamp: '2026-07-03T08:05:00.000Z', source: 'workspace-prep', text: 'seq-one-late-clock' }),
      makeLine({ seq: 2, timestamp: '2026-07-03T08:00:00.000Z', source: 'cleanup', text: 'seq-two-early-clock' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="completed"
        sessionName="plan-issue-339"
        origin={agentOrigin}
        classification="UserFacing"
      />,
      harness,
    )

    await screen.findByText('seq-two-early-clock')
    const items = Array.from(document.querySelectorAll('[data-testid="task-log-lines"] > li')).map((li) => li.textContent ?? '')
    expect(items.findIndex((t) => t.includes('seq-two-early-clock'))).toBeLessThan(
      items.findIndex((t) => t.includes('Model bound')),
    )
    expect(items.findIndex((t) => t.includes('Model bound'))).toBeLessThan(
      items.findIndex((t) => t.includes('seq-one-late-clock')),
    )
    expect(items.findIndex((t) => t.includes('seq-one-late-clock'))).toBeLessThan(
      items.findIndex((t) => t.includes('Session ended')),
    )

    const capture = installDownloadSpy()
    await user.click(await screen.findByTestId('task-log-download-button'))

    await waitFor(() => {
      expect(capture.clicks.length).toBeGreaterThan(0)
    })

    const exported = await readBlobText(capture.blob)
    expect(exported.indexOf('seq-two-early-clock')).toBeLessThan(exported.indexOf('Model bound'))
    expect(exported.indexOf('Model bound')).toBeLessThan(exported.indexOf('seq-one-late-clock'))
    expect(exported.indexOf('seq-one-late-clock')).toBeLessThan(exported.indexOf('Session ended'))
  })

  it('keyword search hides non-matching milestones and keeps matching ones', async () => {
    mockedGetWorkflowRunSessions.mockResolvedValue([
      sessionFixture({
        id: 'session-1',
        sessionName: 'plan-issue-339',
        status: 'completed',
        startedAt: '2026-07-03T08:00:01.000Z',
        completedAt: '2026-07-03T08:01:00.000Z',
        eventSummary: { resolvedModel: 'minimax/MiniMax-M3' },
      }),
    ])
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, timestamp: '2026-07-03T08:05:00.000Z', source: 'cleanup', text: 'final cleanup' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="completed"
        sessionName="plan-issue-339"
        origin={agentOrigin}
        classification="UserFacing"
      />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    await user.type(await screen.findByTestId('task-log-search-input'), 'cleanup')

    await waitFor(() => {
      expect(screen.queryByTestId('task-log-milestone-model-bound')).not.toBeInTheDocument()
      expect(screen.queryByTestId('task-log-milestone-session-ended')).not.toBeInTheDocument()
    })
    expect(screen.getByText('final cleanup')).toBeInTheDocument()

    await user.clear(await screen.findByTestId('task-log-search-input'))
    await user.type(await screen.findByTestId('task-log-search-input'), 'Model bound')

    await waitFor(() => {
      expect(screen.getByTestId('task-log-milestone-model-bound')).toBeInTheDocument()
    })
    expect(screen.queryByText('final cleanup')).not.toBeInTheDocument()
  })

  it('source-chip filtering never hides milestone rows even when the chip would hide ops lines', async () => {
    mockedGetWorkflowRunSessions.mockResolvedValue([
      sessionFixture({
        id: 'session-1',
        sessionName: 'plan-issue-339',
        status: 'completed',
        startedAt: '2026-07-03T08:00:01.000Z',
        completedAt: '2026-07-03T08:01:00.000Z',
        eventSummary: { resolvedModel: 'minimax/MiniMax-M3' },
      }),
    ])
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, timestamp: '2026-07-03T08:05:00.000Z', source: 'workspace-prep', text: 'prep-line' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="completed"
        sessionName="plan-issue-339"
        origin={agentOrigin}
        classification="UserFacing"
      />,
      harness,
    )

    await screen.findByTestId('task-log-panel')
    const chip = await screen.findByTestId('task-log-source-chip-workspace-prep')
    await user.click(chip)

    await waitFor(() => {
      expect(screen.queryByText('prep-line')).not.toBeInTheDocument()
    })
    expect(screen.getByTestId('task-log-milestone-model-bound')).toBeInTheDocument()
    expect(screen.getByTestId('task-log-milestone-session-ended')).toBeInTheDocument()
  })

  it('source-chip set is ops-only — no chip is derived from a milestone row', async () => {
    mockedGetWorkflowRunSessions.mockResolvedValue([
      sessionFixture({
        id: 'session-1',
        sessionName: 'plan-issue-339',
        status: 'completed',
        startedAt: '2026-07-03T08:00:01.000Z',
        completedAt: '2026-07-03T08:01:00.000Z',
        eventSummary: { resolvedModel: 'minimax/MiniMax-M3' },
      }),
    ])
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, source: 'action:rebase', text: 'rebasing' }),
    ]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="completed"
        sessionName="plan-issue-339"
        origin={agentOrigin}
        classification="UserFacing"
      />,
      harness,
    )

    const chipBar = await screen.findByTestId('task-log-source-chips')
    const chips = Array.from(chipBar.querySelectorAll('button')).map((b) => b.textContent?.trim())
    expect(chips).toEqual(['action:rebase'])
  })

  it('exports the filtered merged view with milestone rows serialized as "<timestamp> [session] <label>: <detail>"', async () => {
    mockedGetWorkflowRunSessions.mockResolvedValue([
      sessionFixture({
        id: 'session-1',
        sessionName: 'plan-issue-339',
        status: 'completed',
        startedAt: '2026-07-03T08:00:01.000Z',
        completedAt: '2026-07-03T08:01:00.000Z',
        eventSummary: { resolvedModel: 'minimax/MiniMax-M3' },
      }),
    ])
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, timestamp: '2026-07-03T08:00:00.000Z', source: 'workspace-prep', text: 'before' }),
      makeLine({ seq: 2, timestamp: '2026-07-03T08:02:00.000Z', source: 'cleanup', text: 'after' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="completed"
        sessionName="plan-issue-339"
        origin={agentOrigin}
        classification="UserFacing"
      />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    const capture = installDownloadSpy()
    await user.click(await screen.findByTestId('task-log-download-button'))

    await waitFor(() => {
      expect(capture.clicks.length).toBeGreaterThan(0)
    })

    const blobText = await readBlobText(capture.blob)
    const lines = blobText.split('\n')
    expect(lines).toContain('before')
    expect(lines).toContain('after')
    expect(lines).toContain('2026-07-03T08:00:01.000Z [session] Model bound: minimax/MiniMax-M3')
    expect(lines).toContain('2026-07-03T08:01:00.000Z [session] Session ended: completed')
  })

  it('download applies a keyword filter to milestones (only filtered rows go in the export)', async () => {
    mockedGetWorkflowRunSessions.mockResolvedValue([
      sessionFixture({
        id: 'session-1',
        sessionName: 'plan-issue-339',
        status: 'failed',
        startedAt: '2026-07-03T08:00:01.000Z',
        completedAt: '2026-07-03T08:01:00.000Z',
        failureReason: 'agent stream blew up',
        eventSummary: { resolvedModel: 'minimax/MiniMax-M3' },
      }),
    ])
    const harness = buildHarness(makePage([
      makeLine({ seq: 1, timestamp: '2026-07-03T08:02:00.000Z', source: 'cleanup', text: 'unrelated' }),
    ]), mockedGetIssueWorkflowTaskLog)

    const user = userEvent.setup()
    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="failed"
        sessionName="plan-issue-339"
        origin={agentOrigin}
        classification="UserFacing"
      />,
      harness,
    )

    await screen.findByTestId('task-log-panel')

    await user.type(await screen.findByTestId('task-log-search-input'), 'minimax')

    await waitFor(() => {
      expect(screen.queryByText('unrelated')).not.toBeInTheDocument()
    })

    const capture = installDownloadSpy()
    await user.click(await screen.findByTestId('task-log-download-button'))

    await waitFor(() => {
      expect(capture.clicks.length).toBeGreaterThan(0)
    })

    const blobText = await readBlobText(capture.blob)
    expect(blobText).toContain('Model bound')
    expect(blobText).not.toContain('Session ended')
    expect(blobText).not.toContain('unrelated')
  })

  it('uses the marker with a non-color-only accessible name and a human label prefix', async () => {
    mockedGetWorkflowRunSessions.mockResolvedValue([
      sessionFixture({
        id: 'session-1',
        sessionName: 'plan-issue-339',
        status: 'completed',
        startedAt: '2026-07-03T08:00:01.000Z',
        completedAt: '2026-07-03T08:01:00.000Z',
        eventSummary: { resolvedModel: 'minimax/MiniMax-M3' },
      }),
    ])
    const harness = buildHarness(makePage([]), mockedGetIssueWorkflowTaskLog)

    renderWithHarness(
      <TaskLogPanel
        issueNumber={339}
        taskId="build-task-1"
        workflowRunId="wr-1"
        taskStatus="completed"
        sessionName="plan-issue-339"
        origin={agentOrigin}
        classification="UserFacing"
      />,
      harness,
    )

    const markers = await screen.findAllByTestId('task-log-milestone-marker')
    expect(markers.length).toBeGreaterThan(0)
    for (const marker of markers) {
      expect(marker).toHaveAttribute('aria-label', 'Session event')
    }

    expect(screen.getAllByText((_, el) => el?.textContent?.startsWith('Model bound') ?? false).length).toBeGreaterThan(0)
    expect(screen.getAllByText((_, el) => el?.textContent?.startsWith('Session ended') ?? false).length).toBeGreaterThan(0)
  })
})
