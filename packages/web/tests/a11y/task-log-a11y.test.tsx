// @vitest-environment jsdom
import { afterAll, afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { axe } from 'vitest-axe'
import { ProjectProvider } from '../../src/entities/project'
import { TaskLogPanel } from '../../src/widgets/issue-workflow/ui/TaskLogPanel'
import { server } from '../support/msw'
import type { TaskLogLine, TaskLogPage } from '../../src/entities/issue'
import type { WorkflowRunSession } from '../../src/entities/coder-session/model/types'

const sessionEventHandlers = new Map<string, ((detail: unknown) => void)[]>()

vi.mock('../../src/entities/agent/@x/events', () => ({
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

let taskLogPage: TaskLogPage = { lines: [], nextCursor: null, truncated: false }
let workflowRunSessions: WorkflowRunSession[] = []

const HANDLERS = [
  http.get('*/api/projects/:projectId/issues/:number/workflow/tasks/:taskId/logs', () =>
    HttpResponse.json({ success: true, data: taskLogPage }),
  ),
  http.get('*/api/workflow-runs/:runId/sessions', () =>
    HttpResponse.json({ success: true, data: workflowRunSessions }),
  ),
]

beforeAll(() => {
  server.listen({ onUnhandledRequest: 'error' })
  server.use(...HANDLERS)
})
afterEach(() => {
  cleanup()
  server.resetHandlers()
  server.use(...HANDLERS)
})
afterAll(() => server.close())

function agentSessionFixture(overrides: Partial<WorkflowRunSession>): WorkflowRunSession {
  return {
    id: 'session-id',
    workflowRunId: 'wr-1',
    sessionName: 'plan-issue-339',
    acpSessionId: 'acp-1',
    projectId: 'proj-a11y',
    issueNumber: 339,
    runnerId: null,
    status: 'completed',
    stage: null,
    model: 'minimax/MiniMax-M3',
    workDir: null,
    processPid: null,
    createdAt: '2026-07-03T08:00:00.000Z',
    startedAt: '2026-07-03T08:00:01.000Z',
    completedAt: '2026-07-03T08:01:00.000Z',
    lastDataAt: null,
    failureReason: null,
    exitCode: null,
    ...overrides,
  }
}

const projects = [
  {
    id: 'proj-a11y',
    name: 'a11y-project',
    path: '/tmp/p1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    repositories: [],
  },
]

function makeLine(overrides: Partial<TaskLogLine>): TaskLogLine {
  return {
    seq: 1,
    timestamp: '2026-07-03T08:00:00.000Z',
    source: 'action:rebase',
    text: 'default',
    ...overrides,
  }
}

function makePage(lines: TaskLogLine[], truncated = false): TaskLogPage {
  return { lines: lines.slice().sort((a, b) => a.seq - b.seq), nextCursor: null, truncated }
}

const focusableSelector = [
  'a[href]',
  'button',
  'input',
  'select',
  'textarea',
  '[tabindex]:not([tabindex="-1"])',
].join(',')

describe('TaskLogPanel accessibility structural baseline', () => {
  beforeEach(() => {
    sessionEventHandlers.clear()
    taskLogPage = { lines: [], nextCursor: null, truncated: false }
    workflowRunSessions = []
  })

  afterEach(() => {
    cleanup()
  })

  function renderWithPanel(lines: TaskLogLine[]) {
    taskLogPage = makePage(lines)
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    return render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-a11y">
          <TaskLogPanel issueNumber={161} taskId="build-task-1" workflowRunId="wr-1" taskStatus="failed" />
        </ProjectProvider>
      </QueryClientProvider>,
    )
  }

  it('passes structural axe rules for the panel with multi-source lines', async () => {
    const { container } = renderWithPanel([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'Cloning repo' }),
      makeLine({ seq: 2, source: 'action:rebase', text: 'CONFLICT (content)' }),
      makeLine({ seq: 3, source: 'cleanup', text: 'rm tmp' }),
    ])

    await screen.findByTestId('task-log-panel')
    await screen.findByTestId('task-log-source-chips')

    const results = await axe(container, {
      runOnly: {
        type: 'rule',
        values: [
          'aria-allowed-attr',
          'aria-allowed-role',
          'aria-command-name',
          'aria-dialog-name',
          'aria-hidden-body',
          'aria-hidden-focus',
          'aria-input-field-name',
          'aria-required-attr',
          'aria-required-children',
          'aria-required-parent',
          'aria-roles',
          'aria-toggle-field-name',
          'aria-valid-attr-value',
          'aria-valid-attr',
          'button-name',
          'heading-order',
          'label',
          'tabindex',
        ],
      },
    })

    expect(results.violations).toEqual([])
  })

  it('makes the search input, each source chip, and the download button keyboard-reachable in DOM order', async () => {
    const { container } = renderWithPanel([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'Cloning repo' }),
      makeLine({ seq: 2, source: 'action:rebase', text: 'CONFLICT' }),
      makeLine({ seq: 3, source: 'cleanup', text: 'rm tmp' }),
    ])

    await screen.findByTestId('task-log-panel')

    const search = (await screen.findByTestId('task-log-search-input')) as HTMLInputElement
    expect(search).toHaveAccessibleName(/search log lines/i)

    const rebaseChip = (await screen.findByTestId('task-log-source-chip-action:rebase')) as HTMLButtonElement
    expect(rebaseChip.tagName).toBe('BUTTON')
    expect(rebaseChip.textContent?.trim()).toBe('action:rebase')
    expect(rebaseChip).toHaveAccessibleName('action:rebase')

    const cleanupChip = (await screen.findByTestId('task-log-source-chip-cleanup')) as HTMLButtonElement
    expect(cleanupChip.tagName).toBe('BUTTON')
    expect(cleanupChip).toHaveAccessibleName('cleanup')

    const workspaceChip = (await screen.findByTestId('task-log-source-chip-workspace-prep')) as HTMLButtonElement
    expect(workspaceChip.tagName).toBe('BUTTON')
    expect(workspaceChip).toHaveAccessibleName('workspace-prep')

    const download = (await screen.findByTestId('task-log-download-button')) as HTMLButtonElement
    expect(download.tagName).toBe('BUTTON')
    expect(download).toHaveAccessibleName(/download/i)

    const focusable = Array.from(container.querySelectorAll<HTMLElement>(focusableSelector))
      .filter((el) => el.getAttribute('tabindex') !== '-1' && el.getAttribute('aria-hidden') !== 'true')

    const focusableIds = focusable.map((el) => {
      const testId = el.getAttribute('data-testid')
      if (testId === 'task-log-search-input') return 'search'
      if (testId?.startsWith('task-log-source-chip-')) return `chip:${testId.slice('task-log-source-chip-'.length)}`
      if (testId === 'task-log-download-button') return 'download'
      return null
    })

    expect(focusableIds).toContain('search')
    expect(focusableIds).toContain('chip:action:rebase')
    expect(focusableIds).toContain('chip:cleanup')
    expect(focusableIds).toContain('chip:workspace-prep')
    expect(focusableIds).toContain('download')

    const searchIdx = focusableIds.indexOf('search')
    const rebaseIdx = focusableIds.indexOf('chip:action:rebase')
    const cleanupIdx = focusableIds.indexOf('chip:cleanup')
    const workspaceIdx = focusableIds.indexOf('chip:workspace-prep')
    const downloadIdx = focusableIds.indexOf('download')

    expect(searchIdx).toBeLessThan(downloadIdx)
    expect(rebaseIdx).toBeLessThan(cleanupIdx)
    expect(cleanupIdx).toBeLessThan(workspaceIdx)
  })

  it('tabs through the panel interactive controls in DOM order', async () => {
    const user = userEvent.setup()
    const { container } = renderWithPanel([
      makeLine({ seq: 1, source: 'workspace-prep', text: 'Cloning repo' }),
      makeLine({ seq: 2, source: 'action:rebase', text: 'CONFLICT' }),
      makeLine({ seq: 3, source: 'cleanup', text: 'rm tmp' }),
    ])

    await screen.findByTestId('task-log-panel')

    const interactiveElements = Array.from(container.querySelectorAll<HTMLElement>(focusableSelector))
      .filter((element) => element.getAttribute('tabindex') !== '-1' && !element.hasAttribute('disabled') && element.getAttribute('aria-hidden') !== 'true')

    expect(interactiveElements.length).toBeGreaterThan(0)

    await user.tab()
    expect(interactiveElements[0]).toHaveFocus()

    for (const element of interactiveElements.slice(1)) {
      await user.tab()
      expect(element).toHaveFocus()
    }
  })
})

describe('TaskLogPanel accessibility — milestone rows (Phase 3b)', () => {
  beforeEach(() => {
    sessionEventHandlers.clear()
    taskLogPage = { lines: [], nextCursor: null, truncated: false }
    workflowRunSessions = []
  })

  afterEach(() => {
    cleanup()
  })

  function renderAgentPanelWithLines(lines: TaskLogLine[], sessions: WorkflowRunSession[]) {
    taskLogPage = makePage(lines)
    workflowRunSessions = sessions
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    return render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-a11y">
          <TaskLogPanel
            issueNumber={339}
            taskId="build-task-1"
            workflowRunId="wr-1"
            taskStatus="completed"
            sessionName="plan-issue-339"
            origin={{ uses: 'mohist/acp-agent' }}
            classification="UserFacing"
          />
        </ProjectProvider>
      </QueryClientProvider>,
    )
  }

  it('passes structural axe rules for the panel when a timeline contains both ops lines and milestone rows', async () => {
    const { container } = renderAgentPanelWithLines(
      [makeLine({ seq: 1, source: 'workspace-prep', text: 'Cloning repo' })],
      [agentSessionFixture({ id: 'session-1', sessionName: 'plan-issue-339' })],
    )

    await screen.findByTestId('task-log-panel')
    expect(await screen.findByTestId('task-log-milestone-model-bound')).toBeInTheDocument()

    const results = await axe(container, {
      runOnly: {
        type: 'rule',
        values: [
          'aria-allowed-attr',
          'aria-allowed-role',
          'aria-command-name',
          'aria-dialog-name',
          'aria-hidden-body',
          'aria-hidden-focus',
          'aria-input-field-name',
          'aria-required-attr',
          'aria-required-children',
          'aria-required-parent',
          'aria-roles',
          'aria-toggle-field-name',
          'aria-valid-attr-value',
          'aria-valid-attr',
          'button-name',
          'heading-order',
          'label',
          'tabindex',
        ],
      },
    })

    expect(results.violations).toEqual([])
  })

  it('conveys the milestone marker non-color-only: each marker carries an aria-label, a visible human label prefix, and a glyph', async () => {
    renderAgentPanelWithLines(
      [makeLine({ seq: 1, source: 'workspace-prep', text: 'pre' })],
      [agentSessionFixture({ id: 'session-1', sessionName: 'plan-issue-339' })],
    )

    const markers = await screen.findAllByTestId('task-log-milestone-marker')
    expect(markers.length).toBeGreaterThan(0)
    for (const marker of markers) {
      expect(marker.getAttribute('aria-label')).toBe('Session event')
      expect(marker.tagName.toLowerCase()).toBe('svg')
    }

    expect(screen.getAllByText((_, el) => el?.textContent?.startsWith('Model bound') ?? false).length).toBeGreaterThan(0)
    expect(screen.getAllByText((_, el) => el?.textContent?.startsWith('Session ended') ?? false).length).toBeGreaterThan(0)
  })

  it('does not introduce any new interactive element for the milestone variant (no new focusable targets)', async () => {
    const { container } = renderAgentPanelWithLines(
      [makeLine({ seq: 1, source: 'workspace-prep', text: 'pre' })],
      [agentSessionFixture({ id: 'session-1', sessionName: 'plan-issue-339' })],
    )

    await screen.findByTestId('task-log-panel')

    const interactiveInTimeline = Array.from(container.querySelectorAll<HTMLElement>(focusableSelector))
      .filter((element) => {
        const testId = element.getAttribute('data-testid') ?? ''
        return testId.startsWith('task-log-milestone-')
      })

    expect(interactiveInTimeline).toEqual([])
  })
})
