// @vitest-environment jsdom
/**
 * Shared test harness for the TaskLogPanel colocated test files.
 *
 * Vitest hoists `vi.mock()` per-file, so each `*.test.tsx` must declare its own
 * `vi.mock(...)` / `vi.hoisted(...)` blocks (those cannot be imported). This module
 * exports everything ELSE that the TaskLogPanel render-test files share:
 *   - fixture builders (`makeLine`, `makePage`, `makeEnvelope`)
 *   - the SignalR fake-connection harness (`FakeConnection`, `makeFakeConnection`,
 *     `mockConnectionBuilder`) plus the shared registries `fakeConnections` and
 *     `recordedInvokes`
 *   - the page-test harness (`buildHarness`, `renderWithHarness`,
 *     `flushAndGetLastConnection`), `projects`, and `sessionFixture`
 *   - the download/export spy (`installDownloadSpy`, `readBlobText`)
 *
 * NOTE: `fakeConnections` and `recordedInvokes` are module-level singletons shared
 * across a single test file's imports; each test file resets them in `beforeEach`.
 */
import type { ReactNode } from 'react'
import { act, render, waitFor } from '@testing-library/react'
import { expect, vi, type Mock } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import type { TaskLogLine, TaskLogPage } from '../../../entities/issue'
import type { WorkflowRunSession } from '../../../entities/coder-session/model/types'
import {
  fakeConnections,
  recordedInvokes,
  makeFakeConnection,
  type FakeConnection,
} from '../../../../tests/support/signalr-fake'

export type { FakeConnection }

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

export function makeEnvelope(entries: { seq: number; timestamp?: string; source?: string; text?: string }[], options: { ownerKind?: string; ownerId?: string; workId?: string; taskId?: string | null; truncated?: boolean } = {}): import('../../../shared/api/events-hub').TaskLogDeltaEnvelopeWire {
  return {
    ownerKind: options.ownerKind ?? 'workflow',
    ownerId: options.ownerId ?? 'wr-1',
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
    acpSessionId: 'acp-1',
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


export { fakeConnections, recordedInvokes, makeFakeConnection }

export function mockConnectionBuilder() {
  // 全局 signalr alias 已让 HubConnectionBuilder 返回构建 FakeConnection 的链；
  // 保留此函数为空操作以维持调用方签名不变。
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

export interface TestHarness {
  queryClient: QueryClient
  page: { current: TaskLogPage | undefined }
  setPage: (next: TaskLogPage | undefined) => void
}

export function buildHarness(
  initialPage: TaskLogPage | undefined,
  mockedGetIssueWorkflowTaskLog: Mock,
): TestHarness {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const page = { current: initialPage }
  mockedGetIssueWorkflowTaskLog.mockImplementation(async () => {
    return page.current ?? { lines: [], nextCursor: null, truncated: false }
  })
  return {
    queryClient,
    page,
    setPage(next) {
      page.current = next
    },
  }
}

export function newQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

export function renderWithHarness(ui: ReactNode, harness: TestHarness) {
  return render(
    <QueryClientProvider client={harness.queryClient}>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        {ui}
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

export async function flushAndGetLastConnection(): Promise<FakeConnection> {
  await waitFor(() => {
    expect(fakeConnections.length).toBeGreaterThan(0)
  })
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0))
  })
  return fakeConnections[fakeConnections.length - 1]
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
