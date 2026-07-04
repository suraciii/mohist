// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { HubConnectionBuilder } from '@microsoft/signalr'
import { axe } from 'vitest-axe'
import { ProjectProvider } from '../../src/entities/project'
import { getIssueWorkflowTaskLog } from '../../src/entities/issue'
import { TaskLogPanel } from '../../src/widgets/issue-workflow/ui/TaskLogPanel'
import type { TaskLogLine, TaskLogPage } from '../../src/entities/issue'

vi.mock('@microsoft/signalr', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@microsoft/signalr')>()
  return {
    ...actual,
    HubConnectionBuilder: vi.fn(),
  }
})

vi.mock('../../src/entities/issue/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../src/entities/issue/api/client')>()
  return {
    ...actual,
    getIssueWorkflowTaskLog: vi.fn(),
  }
})

const mockedGetIssueWorkflowTaskLog = vi.mocked(getIssueWorkflowTaskLog)

type Listener = (...args: unknown[]) => void

interface FakeConnection {
  state: number
  onreconnecting: (handler?: Listener) => Listener | null | void
  onreconnected: (handler?: Listener) => Listener | null | void
  onclose: (handler?: Listener) => Listener | null | void
  on: (event: string, handler: Listener) => void
  start: () => Promise<void>
  stop: () => Promise<void>
  invoke: (...args: unknown[]) => Promise<unknown>
  handlers: Map<string, Listener>
  reconnectHandler: Listener | null
}

const fakeConnections: FakeConnection[] = []

function makeFakeConnection(): FakeConnection {
  const handlers = new Map<string, Listener>()
  const conn: FakeConnection = {
    state: 0,
    reconnectHandler: null,
    onreconnecting() {
      if (undefined === undefined) return undefined
    },
    onreconnected(handler) {
      conn.reconnectHandler = handler ?? null
      if (handler === undefined) return undefined
    },
    onclose() {
      if (undefined === undefined) return undefined
    },
    on: vi.fn((event: string, handler: Listener) => {
      handlers.set(event, handler)
    }),
    start: vi.fn(async () => {
      conn.state = 1
    }),
    stop: vi.fn(async () => {
      conn.state = 0
    }),
    invoke: vi.fn(async () => undefined),
    handlers,
  }
  fakeConnections.push(conn)
  return conn
}

function mockConnectionBuilder() {
  function FakeBuilder(this: unknown) {
    return {
      withUrl: () => ({
        withAutomaticReconnect: () => ({
          configureLogging: () => ({
            build: () => makeFakeConnection(),
          }),
        }),
      }),
    }
  }
  vi.mocked(HubConnectionBuilder).mockImplementation(FakeBuilder as unknown as typeof HubConnectionBuilder)
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
    vi.clearAllMocks()
    fakeConnections.length = 0
    mockConnectionBuilder()
    mockedGetIssueWorkflowTaskLog.mockResolvedValue({ lines: [], nextCursor: null, truncated: false })
  })

  afterEach(() => {
    cleanup()
  })

  function renderWithPanel(lines: TaskLogLine[]) {
    const page = makePage(lines)
    mockedGetIssueWorkflowTaskLog.mockImplementation(async () => page)
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
