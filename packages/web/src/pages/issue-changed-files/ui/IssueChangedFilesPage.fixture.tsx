import { afterEach, beforeEach, expect, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { ProjectProvider } from '../../../entities/project'
import { IssueChangedFilesPage } from './IssueChangedFilesPage'
import { useMswServer } from '../../../../tests/support/msw'
import { setScopedValue } from '../../../../tests/support/scoped-property'

export { fireEvent, screen, waitFor }

const FIXTURE_TIME = '2026-07-11T00:00:00.000Z'

function LocationProbe() {
  const location = useLocation()
  return <div data-testid="current-path">{location.pathname}{location.search}</div>
}

export const SAMPLE_ISSUE = {
  id: '1',
  number: 123,
  title: 'Test Issue',
  body: 'Description',
  status: 'in_progress' as const,
  stage: 'build' as const,
  labels: {},
  createdAt: FIXTURE_TIME,
  updatedAt: FIXTURE_TIME,
  projectId: 'proj-1',
  workflowRunId: 'wr-1',
}

const SAMPLE_PROJECT = {
  id: 'proj-1',
  name: 'Test Project',
  createdAt: FIXTURE_TIME,
  updatedAt: FIXTURE_TIME,
  repositories: [],
}

export const SAMPLE_WORKFLOW_RUN_SESSION = {
  id: 'session-1',
  workflowRunId: 'wr-1',
  sessionName: 's-wr-1',
  runtimeSessionId: 'runtime-1',
  projectId: 'proj-1',
  issueNumber: 123,
  runnerId: 'runner-1',
  status: 'active' as const,
  stage: 'build',
  model: 'minimax/MiniMax-M3',
  workDir: null,
  processPid: null,
  createdAt: FIXTURE_TIME,
  startedAt: FIXTURE_TIME,
  completedAt: null,
  lastDataAt: FIXTURE_TIME,
  failureReason: null,
  exitCode: null,
}

export const FOO_DIFF = `diff --git a/src/foo.ts b/src/foo.ts
index 1234567..abcdefg 100644
--- a/src/foo.ts
+++ b/src/foo.ts
@@ -1,3 +1,5 @@
+import React from 'react'
+
 const foo = 'hello'
-const bar = 'world'
+const bar = 'world changed'
+const baz = 'new line'
@@ -10,3 +12,5 @@ export { foo, bar }
 // comment 1
 // comment 2
 // comment 3
+const newCode = 'added'
+export { newCode }`

export const BAR_DIFF = `diff --git a/src/bar.ts b/src/bar.ts
new file mode 100644
--- /dev/null
+++ b/src/bar.ts
@@ -0,0 +1,3 @@
+export const bar = 'new file'
+export const baz = 'another'`

export const HELPER_DIFF = `diff --git a/src/utils/helper.ts b/src/utils/helper.ts
deleted file mode 100644
--- a/src/utils/helper.ts
+++ /dev/null
@@ -1,10 +0,0 @@
-// old code
-export const old = true`

export const SAMPLE_DIFF_DATA = {
  available: true as const,
  reason: null,
  base: 'main',
  head: 'mo/issue-123',
  mergeBase: 'abc123mergebase',
  ahead: 3,
  behind: 0,
  canFastForward: false,
  comparison: 'merge-base' as const,
  summary: { filesChanged: 3, additions: 6, deletions: 2 },
  files: [
    { file: 'src/foo.ts', additions: 4, deletions: 1, diff: FOO_DIFF, isBinary: false },
    { file: 'src/bar.ts', additions: 2, deletions: 0, diff: BAR_DIFF, isBinary: false },
    { file: 'src/utils/helper.ts', additions: 0, deletions: 2, diff: HELPER_DIFF, isBinary: false },
  ],
}

export const SAMPLE_COMMITS_DATA = {
  commits: [{
    hash: 'abc123',
    shortHash: 'abc123',
    message: 'Initial commit\nMore details',
    author: 'Test User',
    date: FIXTURE_TIME,
  }],
}

export function makeLargeDiff(path = 'src/large.txt', lineCount = 350) {
  return `diff --git a/${path} b/${path}
index 1234567..abcdefg 100644
--- a/${path}
+++ b/${path}
@@ -1,${lineCount} +1,${lineCount} @@
${Array.from({ length: lineCount }, (_, index) => (index % 2 === 0 ? `-line ${index}` : `+line ${index}`)).join('\n')}`
}

export interface DiffFile {
  file: string
  additions: number
  deletions: number
  diff: string
  isBinary: boolean
}

export function changedFile(file: string, additions: number, deletions: number, diff: string, isBinary = false): DiffFile {
  return { file, additions, deletions, diff, isBinary }
}

export function withFiles(files: DiffFile[]) {
  return { ...SAMPLE_DIFF_DATA, files }
}

export function useIssueChangedFilesPageFixture() {
  const sessionResponseResolvers = new Set<() => void>()
  const sessionResponsePromises = new Set<Promise<void>>()
  let currentQueryClient: QueryClient | null = null
  const state = {
    issueData: SAMPLE_ISSUE as unknown,
    diffData: SAMPLE_DIFF_DATA as unknown,
    commitsData: SAMPLE_COMMITS_DATA as unknown,
    commitDiffData: {} as Record<string, unknown>,
    sessionsData: [SAMPLE_WORKFLOW_RUN_SESSION] as unknown[],
    fileContentHandler: vi.fn(),
    issueRequestCount: 0,
    diffRequestCount: 0,
    commitsRequestCount: 0,
    sessionsRequestCount: 0,
    sessionsResponseCount: 0,
    blockIssue: false,
    issueError: false,
    blockDiff: false,
    diffError: false,
    blockCommits: false,
    commitsError: false,
    blockSessions: false,
    commitDiffError: false,
    blockCommitDiff: false,
    releaseSessionResponses: async () => {
      sessionResponseResolvers.forEach((resolve) => resolve())
      sessionResponseResolvers.clear()
      await Promise.all(sessionResponsePromises)
      sessionResponsePromises.clear()
    },
    getSessionsQueryStatus: () => currentQueryClient
      ?.getQueryState(['workflow-runs', 'wr-1', 'sessions'])
      ?.status,
    getIssueQueryFetchStatus: () => currentQueryClient
      ?.getQueryState(['issue-detail', 'proj-1', 123])
      ?.fetchStatus,
  }
  const queryClients = new Set<QueryClient>()

  function resetState() {
    state.releaseSessionResponses()
    state.issueData = SAMPLE_ISSUE
    state.diffData = SAMPLE_DIFF_DATA
    state.commitsData = SAMPLE_COMMITS_DATA
    state.commitDiffData = {}
    state.sessionsData = [SAMPLE_WORKFLOW_RUN_SESSION]
    state.fileContentHandler.mockClear()
    state.issueRequestCount = 0
    state.diffRequestCount = 0
    state.commitsRequestCount = 0
    state.sessionsRequestCount = 0
    state.sessionsResponseCount = 0
    state.blockIssue = false
    state.issueError = false
    state.blockDiff = false
    state.diffError = false
    state.blockCommits = false
    state.commitsError = false
    state.blockSessions = false
    state.commitDiffError = false
    state.blockCommitDiff = false
  }

  useMswServer(
    http.get('*/api/projects/:projectId/issues/:issueNumber', () => {
      state.issueRequestCount += 1
      if (state.blockIssue) return new Promise<never>(() => {})
      if (state.issueError) return HttpResponse.json({ success: false, error: 'failed' }, { status: 500 })
      return HttpResponse.json({ success: true, data: state.issueData })
    }),
    http.get('*/api/projects/:projectId/issues/:issueNumber/diff', () => {
      state.diffRequestCount += 1
      if (state.blockDiff) return new Promise<never>(() => {})
      if (state.diffError) return HttpResponse.json({ success: false, error: 'failed' }, { status: 500 })
      return HttpResponse.json({ success: true, data: state.diffData })
    }),
    http.get('*/api/projects/:projectId/issues/:issueNumber/commits', () => {
      state.commitsRequestCount += 1
      if (state.blockCommits) return new Promise<never>(() => {})
      if (state.commitsError) return HttpResponse.json({ success: false, error: 'failed' }, { status: 500 })
      return HttpResponse.json({ success: true, data: state.commitsData })
    }),
    http.get('*/api/projects/:projectId/issues/:issueNumber/commits/:hash/diff', ({ params }) => {
      if (state.blockCommitDiff) return new Promise<never>(() => {})
      if (state.commitDiffError) return HttpResponse.json({ success: false, error: 'failed' }, { status: 500 })
      return HttpResponse.json({ success: true, data: state.commitDiffData[params.hash as string] ?? { diff: '' } })
    }),
    http.get('*/api/projects/:projectId/issues/:issueNumber/file-content', ({ request, params }) => {
      const path = new URL(request.url).searchParams.get('path') ?? ''
      state.fileContentHandler(Number(params.issueNumber), path)
      return HttpResponse.json({ success: true, data: { base: 'old line', head: 'new line' } })
    }),
    http.get('*/api/workflow-runs/:workflowRunId/sessions', async () => {
      state.sessionsRequestCount += 1
      let markResponseComplete: (() => void) | undefined
      if (state.blockSessions) {
        const responseComplete = new Promise<void>((resolve) => {
          markResponseComplete = resolve
        })
        sessionResponsePromises.add(responseComplete)
        await new Promise<void>((resolve) => sessionResponseResolvers.add(resolve))
      }
      state.sessionsResponseCount += 1
      markResponseComplete?.()
      return HttpResponse.json({ success: true, data: state.sessionsData })
    }),
  )

  function createQueryClient() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    queryClients.add(queryClient)
    currentQueryClient = queryClient
    return queryClient
  }

  function routes() {
    return (
      <Routes>
        <Route path="/:projectName/issues/:number/files" element={<IssueChangedFilesPage />} />
        <Route path="/:projectName/issues/:number/workflow/sessions/:sessionName" element={<div data-testid="session-page-stub">Session Page</div>} />
        <Route path="/:projectName/issues/:number" element={<div>Issue Detail Page</div>} />
        <Route path="/issues/:number/files" element={<IssueChangedFilesPage />} />
        <Route path="/issues/:number/workflow/sessions/:sessionName" element={<div data-testid="session-page-stub">Session Page</div>} />
        <Route path="/issues/:number" element={<div>Issue Detail Page</div>} />
      </Routes>
    )
  }

  function createPage(initialRoute: string) {
    const queryClient = createQueryClient()
    return (
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[SAMPLE_PROJECT]}>
          <MemoryRouter initialEntries={[initialRoute]}>
            <LocationProbe />
            {routes()}
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>
    )
  }

  function createPageWithoutLocationProbe(initialRoute: string) {
    const queryClient = createQueryClient()
    return (
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[SAMPLE_PROJECT]}>
          <MemoryRouter initialEntries={[initialRoute]}>
            {routes()}
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>
    )
  }

  function renderPage(initialRoute = '/issues/123/files') {
    return render(createPage(initialRoute))
  }

  beforeEach(() => {
    vi.clearAllMocks()
    sessionStorage.clear()
    setScopedValue(Element.prototype, 'scrollIntoView', vi.fn())
    resetState()
  })

  afterEach(() => {
    cleanup()
    queryClients.forEach((queryClient) => queryClient.clear())
    queryClients.clear()
    currentQueryClient = null
    sessionStorage.clear()
  })

  return { createPageWithoutLocationProbe, renderPage, state }
}

export async function selectOption(label: string, option: string) {
  fireEvent.click(screen.getByRole('combobox', { name: label }))
  await waitFor(() => expect(screen.getByText(option)).toBeTruthy())
  const item = screen.getAllByText(option)
    .map((element) => element.closest('[data-slot="select-item"]') as HTMLElement | null)
    .find((element): element is HTMLElement => element !== null)
  expect(item).toBeTruthy()
  fireEvent.pointerDown(item!)
  fireEvent.pointerUp(item!)
  fireEvent.click(item!)
}
