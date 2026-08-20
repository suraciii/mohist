// @vitest-environment jsdom
/**
 * Shared fixtures, fake connections, providers, and export helpers for the
 * colocated TaskLogPanel tests. Module mocks remain in each test file because
 * Vitest hoists them per file.
 */
import type { ReactNode } from 'react'
import { act, render } from '@testing-library/react'
import { vi } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import type { TaskLogLine, TaskLogPage } from '../../../entities/issue'
import type { WorkflowRunSession } from '../../../entities/coder-session/model/types'
import { LiveEventsContext, type LiveEventsApi, type TaskLogDeltaEnvelopeWire } from '../../../shared/api/live-events'

type TaskLogListener = (delta: TaskLogDeltaEnvelopeWire) => void
export const taskLogListeners = new Map<string, TaskLogListener>()
export const registeredTaskLogScopes: Array<{ workflowRunId: string; taskId: string }> = []

export const fakeLiveEvents: LiveEventsApi = {
  registerTaskLogScope(scope, onDelta) {
    const key = `${scope.workflowRunId}:${scope.taskId}`
    registeredTaskLogScopes.push(scope)
    taskLogListeners.set(key, onDelta)
    return {
      admitted: true,
      dispose: () => taskLogListeners.delete(key),
    }
  },
  registerTranscriptReconciliation() {
    return { dispose: () => {} }
  },
}

export function makeLine(overrides: Partial<TaskLogLine>): TaskLogLine {
  return {
    seq: 1,
    timestamp: '2026-07-03T08:00:00.000Z',
    source: 'action:rebase',
    text: 'default',
    ...overrides,
  }
}

export function makePage(lines: TaskLogLine[], truncated = false): TaskLogPage {
  return { lines: lines.slice().sort((a, b) => a.seq - b.seq), nextCursor: null, truncated }
}

export function makeEnvelope(
  entries: { seq: number; timestamp?: string; source?: string; text?: string }[],
  options: { ownerKind?: string; ownerId?: string; workId?: string; taskId?: string; truncated?: boolean } = {},
): TaskLogDeltaEnvelopeWire {
  return {
    ownerKind: options.ownerKind ?? 'workflow',
    ownerId: options.ownerId ?? 'wr-1',
    projectId: 'proj-1',
    workId: options.workId ?? 'work-1',
    taskId: options.taskId ?? 'build-task-1',
    entries: entries.map((e) => ({
      seq: e.seq,
      timestamp: e.timestamp ?? '2026-07-03T08:00:01.000Z',
      source: e.source ?? 'action:rebase',
      text: e.text ?? `line ${e.seq}`,
    })),
    truncated: options.truncated ?? false,
  }
}

export function sessionFixture(overrides: Partial<WorkflowRunSession>): WorkflowRunSession {
  return {
    id: 'session-id',
    workflowRunId: 'wr-1',
    sessionName: 'plan-issue-339',
    runtimeSessionId: 'runtime-1',
    projectId: 'proj-1',
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

export function mockConnectionBuilder() {
  taskLogListeners.clear()
  registeredTaskLogScopes.length = 0
}

export const projects = [
  {
    id: 'proj-1',
    name: 'Project 1',
    path: '/tmp/p1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    repositories: [],
  },
]

export interface TaskLogTestState {
  queryClient: QueryClient
  page: { current: TaskLogPage | undefined }
  setPage: (next: TaskLogPage | undefined) => void
}

export function newQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

export function renderWithTaskLogProviders(ui: ReactNode, testState: TaskLogTestState) {
  return render(
    <QueryClientProvider client={testState.queryClient}>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        <LiveEventsContext.Provider value={fakeLiveEvents}>{ui}</LiveEventsContext.Provider>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

export async function emitTaskLog(delta: TaskLogDeltaEnvelopeWire): Promise<void> {
  await act(async () => {
    taskLogListeners.get(`${delta.ownerId}:${delta.taskId}`)?.(delta)
  })
}

export interface DownloadCapture {
  blob: Blob | null
  filename: string | null
  clicks: Array<{ download: string; href: string }>
}

export function installDownloadSpy(): DownloadCapture {
  const capture: DownloadCapture = { blob: null, filename: null, clicks: [] }
  vi.spyOn(URL, 'createObjectURL').mockImplementation((obj: Blob | MediaSource) => {
    capture.blob = obj as Blob
    return 'blob:mock-task-log-url'
  })
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined)

  const realCreate = document.createElement.bind(document)
  vi.spyOn(document, 'createElement').mockImplementation(((tag: string, options?: ElementCreationOptions) => {
    const element = realCreate(tag, options)
    if (tag === 'a') {
      Object.defineProperty(element, 'click', {
        value: () => {
          const anchor = element as HTMLAnchorElement
          capture.clicks.push({ download: anchor.download, href: anchor.href })
        },
        configurable: true,
      })
    }
    return element
  }) as typeof document.createElement)

  return capture
}

export async function readBlobText(blob: Blob | null): Promise<string> {
  if (!blob) return ''
  return await blob.text()
}
