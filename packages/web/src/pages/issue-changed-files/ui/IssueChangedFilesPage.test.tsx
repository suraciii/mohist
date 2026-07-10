import { afterEach, describe, expect, it, vi, beforeEach } from 'vitest'
import { cleanup, render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route, useLocation } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { ProjectProvider } from '../../../entities/project'
import { IssueChangedFilesPage } from './IssueChangedFilesPage'
import { parseDiff, parseDiffFiles } from '../../../widgets/issue-changed-files'
import { useMswServer } from '../../../../tests/support/msw'
import { setScopedValue } from '../../../../tests/support/scoped-property'

function LocationProbe() {
  const location = useLocation()
  return <div data-testid="current-path">{location.pathname}{location.search}</div>
}

async function selectOption(label: string, option: string) {
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

let _issueData: unknown = null
let _diffData: unknown = null
let _commitsData: unknown = null
let _commitDiffData: Record<string, unknown> = {}
const _fileContentHandler = vi.fn()

let _blockIssue = false
let _issueError = false
let _blockDiff = false
let _diffError = false
let _blockCommits = false
let _commitsError = false
let _commitDiffError = false
let _blockCommitDiff = false

useMswServer(
  http.get('*/api/projects/:projectId/issues/:issueNumber', () => {
    if (_blockIssue) return new Promise(() => {})
    if (_issueError) return HttpResponse.json({ success: false, error: 'failed' }, { status: 500 })
    return HttpResponse.json({ success: true, data: _issueData })
  }),
  http.get('*/api/projects/:projectId/issues/:issueNumber/diff', () => {
    if (_blockDiff) return new Promise(() => {})
    if (_diffError) return HttpResponse.json({ success: false, error: 'failed' }, { status: 500 })
    return HttpResponse.json({ success: true, data: _diffData })
  }),
  http.get('*/api/projects/:projectId/issues/:issueNumber/commits', () => {
    if (_blockCommits) return new Promise(() => {})
    if (_commitsError) return HttpResponse.json({ success: false, error: 'failed' }, { status: 500 })
    return HttpResponse.json({ success: true, data: _commitsData })
  }),
  http.get('*/api/projects/:projectId/issues/:issueNumber/commits/:hash/diff', ({ params }) => {
    if (_blockCommitDiff) return new Promise(() => {})
    if (_commitDiffError) return HttpResponse.json({ success: false, error: 'failed' }, { status: 500 })
    const hashKey = params.hash as string
    return HttpResponse.json({ success: true, data: _commitDiffData[hashKey] ?? { diff: '' } })
  }),
  http.get('*/api/projects/:projectId/issues/:issueNumber/file-content', ({ request, params }) => {
    const url = new URL(request.url)
    const filePath = url.searchParams.get('path') ?? ''
    _fileContentHandler(Number(params.issueNumber), filePath)
    return HttpResponse.json({ success: true, data: { base: 'old line', head: 'new line' } })
  }),
)

const SAMPLE_ISSUE = {
  id: '1',
  number: 123,
  title: 'Test Issue',
  body: 'Description',
  status: 'in_progress' as const,
  stage: 'build' as const,
  labels: {},
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString(),
  projectId: 'proj-1',
}

const FOO_DIFF = `diff --git a/src/foo.ts b/src/foo.ts
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

const BAR_DIFF = `diff --git a/src/bar.ts b/src/bar.ts
new file mode 100644
--- /dev/null
+++ b/src/bar.ts
@@ -0,0 +1,3 @@
+export const bar = 'new file'
+export const baz = 'another'`

const HELPER_DIFF = `diff --git a/src/utils/helper.ts b/src/utils/helper.ts
deleted file mode 100644
--- a/src/utils/helper.ts
+++ /dev/null
@@ -1,10 +0,0 @@
-// old code
-export const old = true`

const SAMPLE_DIFF_DATA = {
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

const SAMPLE_COMMITS_DATA = {
  commits: [
    {
      hash: 'abc123',
      shortHash: 'abc123',
      message: 'Initial commit\nMore details',
      author: 'Test User',
      date: new Date().toISOString(),
    },
  ],
}

function makeLargeDiff(path = 'src/large.txt', lineCount = 350) {
  return `diff --git a/${path} b/${path}
index 1234567..abcdefg 100644
--- a/${path}
+++ b/${path}
@@ -1,${lineCount} +1,${lineCount} @@
${Array.from({ length: lineCount }, (_, i) => (i % 2 === 0 ? `-line ${i}` : `+line ${i}`)).join('\n')}`
}

function setupDefaults() {
  _issueData = SAMPLE_ISSUE
  _diffData = SAMPLE_DIFF_DATA
  _commitsData = SAMPLE_COMMITS_DATA
  _commitDiffData = {}
  _blockIssue = false
  _issueError = false
  _blockDiff = false
  _diffError = false
  _blockCommits = false
  _commitsError = false
  _commitDiffError = false
  _blockCommitDiff = false
}

const queryClients = new Set<QueryClient>()

function createQueryClient() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  queryClients.add(queryClient)
  return queryClient
}

function renderPage(initialRoute = '/issues/123/files') {
  const queryClient = createQueryClient()

  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1">
        <MemoryRouter initialEntries={[initialRoute]}>
          <LocationProbe />
          <Routes>
            <Route path="/issues/:number/files" element={<IssueChangedFilesPage />} />
            <Route path="/issues/:number" element={<div>Issue Detail Page</div>} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>
  )
}

describe('IssueChangedFilesPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    sessionStorage.clear()
    setScopedValue(Element.prototype, 'scrollIntoView', vi.fn())
    setupDefaults()
  })

  afterEach(() => {
    cleanup()
    queryClients.forEach((queryClient) => queryClient.clear())
    queryClients.clear()
    sessionStorage.clear()
  })

  describe('route rendering', () => {
    it('renders the changed-files page at the dedicated route', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('main')).toBeTruthy()
      expect(screen.getByText('mo/issue-123')).toBeTruthy()
      expect(screen.getByText('files changed')).toBeTruthy()
    })

    it('renders issue number and title in header', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('#123')).toBeTruthy()
    })

    it('renders diffstat with additions and deletions', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getAllByText('+6').length).toBeGreaterThan(0)
      expect(screen.getAllByText('-2').length).toBeGreaterThan(0)
    })
  })

  describe('unavailable states', () => {
    it('shows not-started message when reason is not_started', async () => {
      _diffData = { available: false, reason: 'not_started', message: '' }
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('No changes yet')).toBeTruthy()
    })

    it('shows workspace-removed message when workspace was removed', async () => {
      _diffData = { available: false, reason: 'workspace_removed', message: '' }
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('Changes unavailable — workspace removed')).toBeTruthy()
    })

    it('shows branch-missing message when branch is missing', async () => {
      _diffData = { available: false, reason: 'branch_missing', message: '' }
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('Changes unavailable — branch missing')).toBeTruthy()
    })

    it('renders loading state while issue is loading', () => {
      _blockIssue = true
      renderPage()
      expect(screen.getByText('Loading...')).toBeTruthy()
    })

    it('shows recoverable error when issue API fails', async () => {
      _issueError = true
      _issueData = undefined
      renderPage()
      await waitFor(() => {
        expect(screen.getByText('Failed to load issue details.')).toBeTruthy()
      })
      expect(screen.getByText('View issue detail')).toBeTruthy()
    })

    it('shows recoverable error when diff API fails', async () => {
      _diffError = true
      _diffData = undefined
      renderPage()
      await waitFor(() => {
        expect(screen.getByText('Failed to load issue diff.')).toBeTruthy()
      })
      expect(screen.getByText('View issue detail')).toBeTruthy()
    })

    it('shows recoverable error when commits API fails', async () => {
      _commitsError = true
      _commitsData = undefined
      renderPage()
      await waitFor(() => {
        expect(screen.getByText('Failed to load issue commits.')).toBeTruthy()
      })
      expect(screen.getByText('View issue detail')).toBeTruthy()
    })

    it('shows recoverable error when commit diff API fails', async () => {
      _commitDiffError = true
      renderPage()
      await screen.findByText('Test Issue')
      await selectOption('Commit view', 'abc123: Initial commit')

      await waitFor(() => {
        expect(screen.getByText('Failed to load commit diff.')).toBeTruthy()
        expect(screen.getAllByText('Exit commit mode').length).toBeGreaterThan(0)
        expect(screen.getAllByText('Back to issue').length).toBeGreaterThan(0)
      })
    })
  })

  describe('View files navigation from Issue Detail', () => {
    it('has a back button that navigates to Issue Detail', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      const backButton = screen.getByText('Back to issue')
      expect(backButton).toBeTruthy()
      fireEvent.click(backButton)
    })
  })

  describe('reader controls', () => {
    it('renders expand all and collapse all buttons', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('Expand all')).toBeTruthy()
      expect(screen.getByText('Collapse all')).toBeTruthy()
    })

    it('renders split view toggle', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByRole('button', { name: /split view/i })).toBeTruthy()
    })

    it('toggles to split view mode', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      const splitButton = screen.getByRole('button', { name: /split view/i })
      fireEvent.click(splitButton)
      expect(screen.getByRole('button', { name: /unified view/i })).toBeTruthy()
    })
  })

  describe('directory-grouped file tree', () => {
    it('renders the file tree with directories', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('src/')).toBeTruthy()
      expect(screen.getByText('utils/')).toBeTruthy()
    })

    it('renders file entries with addition/deletion counts', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('foo.ts')).toBeTruthy()
    })

    it('allows selecting a file from the tree', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      const expandButton = screen.getByText('Expand all')
      fireEvent.click(expandButton)
      await waitFor(() => {
        expect(screen.getAllByText('foo.ts').length).toBeGreaterThan(0)
      })
    })

    it('filters files by path when filter is entered', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      const filterInput = screen.getByPlaceholderText('Filter files...')
      fireEvent.change(filterInput, { target: { value: 'bar' } })
      await waitFor(() => {
        expect(screen.queryByText('foo.ts')).toBeNull()
        expect(screen.getByText('bar.ts')).toBeTruthy()
      })
    })

    it('shows no matching files when filter has no results', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      const filterInput = screen.getByPlaceholderText('Filter files...')
      fireEvent.change(filterInput, { target: { value: 'nonexistent' } })
      await waitFor(() => {
        expect(screen.getByText('No matching files')).toBeTruthy()
      })
    })

    it('shows empty state when issue has no diff entries', async () => {
      _diffData = { ...SAMPLE_DIFF_DATA, files: [] }
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('No file changes yet')).toBeTruthy()
    })
  })

  describe('large-diff Render anyway behavior', () => {
    it('renders the reader shell with diff controls', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('Expand all')).toBeTruthy()
    })

    it('shows large diff placeholder when file exceeds threshold', async () => {
      const largeDiff = `diff --git a/src/large.txt b/src/large.txt
index 1234567..abcdefg 100644
--- a/src/large.txt
+++ b/src/large.txt
@@ -1,350 +1,350 @@
${Array.from({ length: 350 }, (_, i) => (i % 2 === 0 ? `-line ${i}` : `+line ${i}`)).join('\n')}`
      const largeDiffData = {
        ...SAMPLE_DIFF_DATA,
        files: [
          { file: 'src/large.txt', additions: 175, deletions: 175, diff: largeDiff, isBinary: false },
        ],
      }
      _diffData = largeDiffData
      renderPage()
      await screen.findByText('Test Issue')
      const largeFileEntry = screen.getByText('large.txt')
      fireEvent.click(largeFileEntry)
      await waitFor(() => {
        expect(screen.getByText(/Large diff/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
    })

    it('renders large diff when Render anyway is clicked', async () => {
      const largeDiff = makeLargeDiff()
      const largeDiffData = {
        ...SAMPLE_DIFF_DATA,
        files: [
          { file: 'src/large.txt', additions: 175, deletions: 175, diff: largeDiff, isBinary: false },
        ],
      }
      _diffData = largeDiffData
      renderPage()
      await screen.findByText('Test Issue')
      const largeFileEntry = screen.getByText('large.txt')
      fireEvent.click(largeFileEntry)
      await waitFor(() => {
        expect(screen.getByText(/Large diff/)).toBeTruthy()
      })
      const renderAnywayButton = screen.getByText('Render anyway')
      fireEvent.click(renderAnywayButton)
      await waitFor(() => {
        expect(screen.queryByText(/Large diff/)).toBeNull()
      })
    })

    it('keeps Render anyway active for the selected file across reader modes', async () => {
      const largeDiffData = {
        ...SAMPLE_DIFF_DATA,
        files: [
          { file: 'src/large.txt', additions: 175, deletions: 175, diff: makeLargeDiff(), isBinary: false },
        ],
      }
      _diffData = largeDiffData

      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('large.txt'))
      await waitFor(() => {
        expect(screen.getByText(/Large diff/)).toBeTruthy()
      })
      fireEvent.click(screen.getByText('Render anyway'))
      await waitFor(() => {
        expect(screen.queryByText(/Large diff/)).toBeNull()
      })

      fireEvent.click(screen.getByRole('button', { name: /split view/i }))
      expect(screen.queryByText(/Large diff/)).toBeNull()

      await selectOption('Reader mode', 'Raw')
      expect(screen.queryByText(/Large diff/)).toBeNull()
      expect(screen.getByText('Copy')).toBeTruthy()

      await selectOption('Reader mode', 'Search')
      expect(screen.queryByText(/Large diff/)).toBeNull()
      expect(screen.getByPlaceholderText('Search diff...')).toBeTruthy()

      await selectOption('Reader mode', 'Full file')
      await waitFor(() => {
        expect(screen.queryByText(/Large diff/)).toBeNull()
        expect(_fileContentHandler).toHaveBeenCalled()
      })
    })
  })

  describe('unified diff rendering', () => {
    it('renders page with diff controls', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('Expand all')).toBeTruthy()
      expect(screen.getByText('Collapse all')).toBeTruthy()
    })
  })

  describe('prev/next hunk navigation', () => {
    it('renders mode buttons', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByRole('combobox', { name: 'Reader mode' }))
      expect(screen.getAllByText('Diff').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Raw').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Full file').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Search').length).toBeGreaterThan(0)
    })
  })

  describe('raw patch mode', () => {
    it('renders raw patch content when Raw mode is selected', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      const fooFile = screen.getByText('foo.ts')
      fireEvent.click(fooFile)
      await selectOption('Reader mode', 'Raw')
      await waitFor(() => {
        expect(screen.getByText('Copy')).toBeTruthy()
      })
    })
  })

  describe('search within diff', () => {
    it('can interact with the Search button', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByRole('combobox', { name: 'Reader mode' }))
      expect(screen.getByText('Search')).toBeTruthy()
    })
  })

  describe('commit-scoped reading', () => {
    it('shows commit selector when commits are available', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('View commit...')).toBeTruthy()
    })

    it('enters commit mode when a commit is selected', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      await selectOption('Commit view', 'abc123: Initial commit')
      await waitFor(() => {
        expect(screen.getByText('Exit commit mode')).toBeTruthy()
      })
    })

    it('exits commit mode when Exit commit mode is clicked', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      await selectOption('Commit view', 'abc123: Initial commit')
      await waitFor(() => {
        expect(screen.getByText('Exit commit mode')).toBeTruthy()
      })
      const exitButton = screen.getByText('Exit commit mode')
      fireEvent.click(exitButton)
      await waitFor(() => {
        expect(screen.queryByText('Exit commit mode')).toBeNull()
      })
    })
  })

  describe('reading position restoration', () => {
    it('can interact with reader controls', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('Expand all')).toBeTruthy()
    })
  })

  describe('direct route loading', () => {
    it('renders the page without blank root when diff data is available', async () => {
      const { container } = renderPage()
      await screen.findByText('Test Issue')
      expect(container.firstChild).not.toBeNull()
    })

    it('renders the same content via direct route as via navigation', async () => {
      renderPage('/issues/123/files')
      await screen.findByText('Test Issue')
      expect(screen.getByText('#123')).toBeTruthy()
    })

    it('renders files page when issue number is valid and diff is available', async () => {
      renderPage('/issues/123/files')
      await screen.findByText('Test Issue')
      expect(screen.getByText('main')).toBeTruthy()
      expect(screen.getByText('mo/issue-123')).toBeTruthy()
      expect(screen.getByText('files changed')).toBeTruthy()
    })

    it('does not leave React root blank on direct load', async () => {
      const { container } = renderPage('/issues/123/files')
      await screen.findByText('Test Issue')
      const root = container.querySelector('#root') || container.firstChild
      expect(root?.textContent).not.toBe('')
    })
  })

  describe('refresh-equivalent initial routing', () => {
    it('renders the files page with fresh MemoryRouter entry', async () => {
      const queryClient = createQueryClient()
      const { container } = render(
        <QueryClientProvider client={queryClient}>
          <ProjectProvider initialProjectId="proj-1">
            <MemoryRouter initialEntries={['/issues/123/files']}>
              <Routes>
                <Route path="/issues/:number/files" element={<IssueChangedFilesPage />} />
                <Route path="/issues/:number" element={<div>Issue Detail Page</div>} />
              </Routes>
            </MemoryRouter>
          </ProjectProvider>
        </QueryClientProvider>
      )
      await screen.findByText('Test Issue')
      expect(container.firstChild).not.toBeNull()
    })

    it('renders issue header and diff metadata on fresh route entry', async () => {
      renderPage('/issues/123/files')
      await screen.findByText('Test Issue')
      expect(screen.getByText('#123')).toBeTruthy()
      expect(screen.getByText('main')).toBeTruthy()
      expect(screen.getAllByText('+6').length).toBeGreaterThan(0)
      expect(screen.getAllByText('-2').length).toBeGreaterThan(0)
    })
  })

  describe('recoverable error UI', () => {
    it('renders visible error state when issue API fails', async () => {
      _issueError = true
      _issueData = undefined
      renderPage()
      await waitFor(() => {
        expect(screen.getByText('Failed to load issue details.')).toBeTruthy()
      })
      expect(screen.getByText('View issue detail')).toBeTruthy()
    })

    it('renders visible error state when diff API fails', async () => {
      _diffError = true
      _diffData = undefined
      renderPage()
      await waitFor(() => {
        expect(screen.getByText('Failed to load issue diff.')).toBeTruthy()
      })
      expect(screen.getByText('View issue detail')).toBeTruthy()
    })

    it('renders visible error state when commits API fails', async () => {
      _commitsError = true
      _commitsData = undefined
      renderPage()
      await waitFor(() => {
        expect(screen.getByText('Failed to load issue commits.')).toBeTruthy()
      })
      expect(screen.getByText('View issue detail')).toBeTruthy()
    })

    it('shows error with path back to issue detail page', async () => {
      _issueError = true
      _issueData = undefined
      renderPage()
      await waitFor(() => {
        expect(screen.getByText('View issue detail')).toBeTruthy()
      })
      const backLink = screen.getByText('View issue detail')
      expect(backLink).toBeTruthy()
      fireEvent.click(backLink)
      expect(screen.getByTestId('current-path').textContent).toBe('/issues/123')
    })

    it('renders error state for invalid issue number', () => {
      const queryClient = createQueryClient()
      render(
        <QueryClientProvider client={queryClient}>
          <ProjectProvider initialProjectId="proj-1">
            <MemoryRouter initialEntries={['/issues/invalid/files']}>
              <Routes>
                <Route path="/issues/:number/files" element={<IssueChangedFilesPage />} />
              </Routes>
            </MemoryRouter>
          </ProjectProvider>
        </QueryClientProvider>
      )
      expect(screen.getByText('Invalid issue number')).toBeTruthy()
    })

    it('renders recoverable error for unavailable diff with reason', async () => {
      _diffData = { available: false, reason: 'workspace_removed', message: 'workspace removed' }
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('Changes unavailable — workspace removed')).toBeTruthy()
    })
  })

  describe('non-eager default rendering', () => {
    it('does not render every line of every changed file on initial load', async () => {
      const { container } = renderPage()
      await screen.findByText('Test Issue')
      const tableRows = container.querySelectorAll('tbody tr')
      expect(tableRows.length).toBeLessThan(500)
    })

    it('shows summary prompt when no file is auto-selected', async () => {
      const allLargeDiffData = {
        ...SAMPLE_DIFF_DATA,
        files: [
          { file: 'package-lock.json', additions: 500, deletions: 200, diff: `diff --git a/package-lock.json b/package-lock.json\n--- a/package-lock.json\n+++ b/package-lock.json\n@@ -1,500 +1,500 @@\n${Array.from({ length: 500 }, (_, i) => (i % 2 === 0 ? `-line ${i}` : `+line ${i}`)).join('\n')}`, isBinary: false },
          { file: 'yarn.lock', additions: 400, deletions: 150, diff: `diff --git a/yarn.lock b/yarn.lock\n--- a/yarn.lock\n+++ b/yarn.lock\n@@ -1,400 +1,400 @@\n${Array.from({ length: 400 }, (_, i) => (i % 2 === 0 ? `-line ${i}` : `+line ${i}`)).join('\n')}`, isBinary: false },
        ],
      }
      _diffData = allLargeDiffData
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('Select a file from the tree to read its diff')).toBeTruthy()
    })

    it('does not render all-files patch stream on initial load', async () => {
      const { container } = renderPage()
      await screen.findByText('Test Issue')
      const diffPanes = container.querySelectorAll('[class*="sticky"]')
      expect(diffPanes.length).toBeLessThanOrEqual(1)
    })

    it('auto-selects first readable non-generated file by default', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('foo.ts')).toBeTruthy()
      const diffContent = screen.getAllByText(/\+import React/)
      expect(diffContent).toBeTruthy()
    })

    it('does not select lockfile as default file', async () => {
      const diffWithLockfile = {
        ...SAMPLE_DIFF_DATA,
        files: [
          { file: 'package-lock.json', additions: 400, deletions: 100, diff: `diff --git a/package-lock.json b/package-lock.json\n--- a/package-lock.json\n+++ b/package-lock.json\n@@ -1,400 +1,400 @@\n${Array.from({ length: 400 }, (_, i) => (i % 2 === 0 ? `-line ${i}` : `+line ${i}`)).join('\n')}`, isBinary: false },
          { file: 'src/foo.ts', additions: 4, deletions: 1, diff: FOO_DIFF, isBinary: false },
        ],
      }
      _diffData = diffWithLockfile
      renderPage()
      await screen.findByText('Test Issue')
      const diffContent = screen.getAllByText(/\+import React/)
      expect(diffContent).toBeTruthy()
    })

    it('keeps metadata-only binary files visible instead of showing the empty diff state', async () => {
      const allBinaryData = {
        ...SAMPLE_DIFF_DATA,
        summary: { filesChanged: 1, additions: 0, deletions: 0 },
        files: [
          { file: 'image.png', additions: 0, deletions: 0, diff: '', isBinary: true },
        ],
      }
      _diffData = allBinaryData
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('image.png')).toBeTruthy()
      expect(screen.queryByText('No file changes yet')).toBeNull()
      expect(screen.getByText('Select a file from the tree to read its diff')).toBeTruthy()
      expect(screen.getByText('1 file changed')).toBeTruthy()
    })
  })

  describe('lockfile and generated large-diff collapse', () => {
    it('shows collapsed placeholder for lockfile by default', async () => {
      const lockfileDiff = `diff --git a/package-lock.json b/package-lock.json\nindex 1234567..abcdefg 100644\n--- a/package-lock.json\n+++ b/package-lock.json\n@@ -1,400 +1,400 @@\n${Array.from({ length: 400 }, (_, i) => (i % 2 === 0 ? `-line ${i}` : `+line ${i}`)).join('\n')}`
      const lockfileDiffData = {
        ...SAMPLE_DIFF_DATA,
        files: [
          { file: 'package-lock.json', additions: 200, deletions: 200, diff: lockfileDiff, isBinary: false },
          { file: 'src/foo.ts', additions: 4, deletions: 1, diff: FOO_DIFF, isBinary: false },
        ],
      }
      _diffData = lockfileDiffData
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('package-lock.json'))
      await waitFor(() => {
        expect(screen.getByText(/Lockfile/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
    })

    it('shows collapsed placeholder for generated file by default', async () => {
      const generatedDiff = `diff --git a/dist/bundle.js b/dist/bundle.js\nindex 1234567..abcdefg 100644\n--- a/dist/bundle.js\n+++ b/dist/bundle.js\n@@ -1,400 +1,400 @@\n${Array.from({ length: 400 }, (_, i) => (i % 2 === 0 ? `-line ${i}` : `+line ${i}`)).join('\n')}`
      const generatedDiffData = {
        ...SAMPLE_DIFF_DATA,
        files: [
          { file: 'dist/bundle.js', additions: 200, deletions: 200, diff: generatedDiff, isBinary: false },
        ],
      }
      _diffData = generatedDiffData
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('bundle.js'))
      await waitFor(() => {
        expect(screen.getByText(/Generated file/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
    })

    it('shows changed-line count in collapsed placeholder', async () => {
      const largeDiff = `diff --git a/src/large.txt b/src/large.txt\nindex 1234567..abcdefg 100644\n--- a/src/large.txt\n+++ b/src/large.txt\n@@ -1,350 +1,350 @@\n${Array.from({ length: 350 }, (_, i) => (i % 2 === 0 ? `-line ${i}` : `+line ${i}`)).join('\n')}`
      const largeDiffData = {
        ...SAMPLE_DIFF_DATA,
        files: [
          { file: 'src/large.txt', additions: 175, deletions: 175, diff: largeDiff, isBinary: false },
        ],
      }
      _diffData = largeDiffData
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('large.txt'))
      await waitFor(() => {
        expect(screen.getByText(/350 lines changed/)).toBeTruthy()
      })
    })

    it('renders lockfile content when Render anyway is clicked', async () => {
      const lockfileDiff = `diff --git a/package-lock.json b/package-lock.json\nindex 1234567..abcdefg 100644\n--- a/package-lock.json\n+++ b/package-lock.json\n@@ -1,200 +1,200 @@\n${Array.from({ length: 200 }, (_, i) => `-line ${i}`).join('\n')}`
      const lockfileDiffData = {
        ...SAMPLE_DIFF_DATA,
        files: [
          { file: 'package-lock.json', additions: 100, deletions: 100, diff: lockfileDiff, isBinary: false },
        ],
      }
      _diffData = lockfileDiffData
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('package-lock.json'))
      await waitFor(() => {
        expect(screen.getByText(/Lockfile/)).toBeTruthy()
      })
      const renderAnywayButton = screen.getByText('Render anyway')
      fireEvent.click(renderAnywayButton)
      await waitFor(() => {
        expect(screen.queryByText(/Lockfile/)).toBeNull()
      })
    })

    it('keeps non-selected files collapsed after Render anyway', async () => {
      const multiFileDiff = {
        ...SAMPLE_DIFF_DATA,
        files: [
          { file: 'package-lock.json', additions: 200, deletions: 200, diff: `diff --git a/package-lock.json b/package-lock.json\n--- a/package-lock.json\n+++ b/package-lock.json\n@@ -1,200 +1,200 @@\n${Array.from({ length: 200 }, (_, i) => (i % 2 === 0 ? `-line ${i}` : `+line ${i}`)).join('\n')}`, isBinary: false },
          { file: 'yarn.lock', additions: 150, deletions: 150, diff: `diff --git a/yarn.lock b/yarn.lock\n--- a/yarn.lock\n+++ b/yarn.lock\n@@ -1,150 +1,150 @@\n${Array.from({ length: 150 }, (_, i) => (i % 2 === 0 ? `-line ${i}` : `+line ${i}`)).join('\n')}`, isBinary: false },
        ],
      }
      _diffData = multiFileDiff
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('package-lock.json'))
      await waitFor(() => {
        expect(screen.getByText(/Lockfile/)).toBeTruthy()
      })
      fireEvent.click(screen.getByText('Render anyway'))
      await waitFor(() => {
        expect(screen.queryByText(/Lockfile/)).toBeNull()
      })
      fireEvent.click(screen.getByText('yarn.lock'))
      await waitFor(() => {
        expect(screen.getByText(/Lockfile/)).toBeTruthy()
      })
    })

    it('resets Render anyway state when navigating to a different issue', async () => {
      const lockfileDiff = `diff --git a/package-lock.json b/package-lock.json\nindex 1234567..abcdefg 100644\n--- a/package-lock.json\n+++ b/package-lock.json\n@@ -1,200 +1,200 @@\n${Array.from({ length: 200 }, (_, i) => `-line ${i}`).join('\n')}`
      _diffData = {
        ...SAMPLE_DIFF_DATA,
        files: [
          { file: 'package-lock.json', additions: 100, deletions: 100, diff: lockfileDiff, isBinary: false },
        ],
      }

      const { rerender } = renderPage('/issues/123/files')

      await screen.findByText('Test Issue')

      fireEvent.click(screen.getByText('package-lock.json'))
      await waitFor(() => {
        expect(screen.getByText(/Lockfile/)).toBeTruthy()
      })
      fireEvent.click(screen.getByText('Render anyway'))
      await waitFor(() => {
        expect(screen.queryByText(/Lockfile/)).toBeNull()
      })

      _issueData = { ...SAMPLE_ISSUE, number: 124, title: 'Another Issue' }
      _diffData = {
        ...SAMPLE_DIFF_DATA,
        head: 'mo/issue-124',
        files: [
          { file: 'package-lock.json', additions: 100, deletions: 100, diff: lockfileDiff, isBinary: false },
        ],
      }

      rerender(
        <QueryClientProvider client={createQueryClient()}>
          <ProjectProvider initialProjectId="proj-1">
            <MemoryRouter initialEntries={['/issues/124/files']}>
              <Routes>
                <Route path="/issues/:number/files" element={<IssueChangedFilesPage />} />
                <Route path="/issues/:number" element={<div>Issue Detail Page</div>} />
              </Routes>
            </MemoryRouter>
          </ProjectProvider>
        </QueryClientProvider>
      )

      await waitFor(() => {
        expect(screen.getByText('Another Issue')).toBeTruthy()
        expect(screen.getByText(/Lockfile/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
    })

    it('applies large-diff collapse in split view mode', async () => {
      const largeDiff = `diff --git a/src/large.txt b/src/large.txt\nindex 1234567..abcdefg 100644\n--- a/src/large.txt\n+++ b/src/large.txt\n@@ -1,350 +1,350 @@\n${Array.from({ length: 350 }, (_, i) => (i % 2 === 0 ? `-line ${i}` : `+line ${i}`)).join('\n')}`
      const largeDiffData = {
        ...SAMPLE_DIFF_DATA,
        files: [
          { file: 'src/large.txt', additions: 175, deletions: 175, diff: largeDiff, isBinary: false },
        ],
      }
      _diffData = largeDiffData
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('large.txt'))
      const splitButton = screen.getByRole('button', { name: /split view/i })
      fireEvent.click(splitButton)
      await waitFor(() => {
        expect(screen.getByText(/Large diff/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
    })

    it('applies large-diff collapse in raw patch mode', async () => {
      const largeDiff = `diff --git a/src/large.txt b/src/large.txt\nindex 1234567..abcdefg 100644\n--- a/src/large.txt\n+++ b/src/large.txt\n@@ -1,350 +1,350 @@\n${Array.from({ length: 350 }, (_, i) => (i % 2 === 0 ? `-line ${i}` : `+line ${i}`)).join('\n')}`
      const largeDiffData = {
        ...SAMPLE_DIFF_DATA,
        files: [
          { file: 'src/large.txt', additions: 175, deletions: 175, diff: largeDiff, isBinary: false },
        ],
      }
      _diffData = largeDiffData
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('large.txt'))
      await selectOption('Reader mode', 'Raw')
      await waitFor(() => {
        expect(screen.getByText(/Large diff/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
    })

    it('applies large-diff collapse in full file mode', async () => {
      _fileContentHandler.mockClear()
      const largeDiff = `diff --git a/src/large.txt b/src/large.txt\nindex 1234567..abcdefg 100644\n--- a/src/large.txt\n+++ b/src/large.txt\n@@ -1,350 +1,350 @@\n${Array.from({ length: 350 }, (_, i) => (i % 2 === 0 ? `-line ${i}` : `+line ${i}`)).join('\n')}`
      const largeDiffData = {
        ...SAMPLE_DIFF_DATA,
        files: [
          { file: 'src/large.txt', additions: 175, deletions: 175, diff: largeDiff, isBinary: false },
        ],
      }
      _diffData = largeDiffData
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('large.txt'))
      await selectOption('Reader mode', 'Full file')
      await waitFor(() => {
        expect(screen.getByText(/Large diff/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
      expect(_fileContentHandler).not.toHaveBeenCalled()
    })

    it('applies large-diff collapse in search mode', async () => {
      const largeDiff = `diff --git a/src/large.txt b/src/large.txt\nindex 1234567..abcdefg 100644\n--- a/src/large.txt\n+++ b/src/large.txt\n@@ -1,350 +1,350 @@\n${Array.from({ length: 350 }, (_, i) => (i % 2 === 0 ? `-line ${i}` : `+line ${i}`)).join('\n')}`
      const largeDiffData = {
        ...SAMPLE_DIFF_DATA,
        files: [
          { file: 'src/large.txt', additions: 175, deletions: 175, diff: largeDiff, isBinary: false },
        ],
      }
      _diffData = largeDiffData
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('large.txt'))
      await selectOption('Reader mode', 'Search')
      await waitFor(() => {
        expect(screen.getByText(/Large diff/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
    })
  })

  describe('no duplicate file headers', () => {
    it('renders only one file header for selected file in unified mode', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('foo.ts'))
      await waitFor(() => {
        const headers = screen.getAllByText('foo.ts')
        expect(headers.length).toBe(1)
      })
    })

    it('renders only one file header for selected file in split mode', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('foo.ts'))
      const splitButton = screen.getByRole('button', { name: /split view/i })
      fireEvent.click(splitButton)
      await waitFor(() => {
        const headers = screen.getAllByText('foo.ts')
        expect(headers.length).toBe(1)
      })
    })

    it('does not duplicate file header when switching modes', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('foo.ts'))
      await waitFor(() => {
        expect(screen.getAllByText('foo.ts').length).toBe(1)
      })
      const splitButton = screen.getByRole('button', { name: /split view/i })
      fireEvent.click(splitButton)
      await waitFor(() => {
        expect(screen.getAllByText('foo.ts').length).toBe(1)
      })
      const unifiedButton = screen.getByRole('button', { name: /unified view/i })
      fireEvent.click(unifiedButton)
      await waitFor(() => {
        expect(screen.getAllByText('foo.ts').length).toBe(1)
      })
    })

    it('file tree entry click selects file with single header', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('foo.ts'))
      await waitFor(() => {
        const fooEntries = screen.getAllByText('foo.ts')
        expect(fooEntries.length).toBe(1)
      })
      fireEvent.click(screen.getByText('bar.ts'))
      await waitFor(() => {
        const barEntries = screen.getAllByText('bar.ts')
        expect(barEntries.length).toBe(1)
      })
    })

    it('no duplicate headers when collapsing and expanding files', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      const expandButton = screen.getByText('Expand all')
      fireEvent.click(expandButton)
      fireEvent.click(screen.getByText('Collapse all'))
      fireEvent.click(screen.getByText('src/'))
      fireEvent.click(screen.getByText('foo.ts'))
      await waitFor(() => {
        const headers = screen.getAllByText('foo.ts')
        expect(headers.length).toBe(1)
      })
    })
  })

  describe('diff parser regressions', () => {
    it('finalizes every file block and does not duplicate hunk lines', () => {
      const blocks = parseDiff(`${FOO_DIFF}\n${BAR_DIFF}`)

      expect(blocks).toHaveLength(2)
      expect(blocks[0].changedLineCount).toBe(7)
      expect(blocks[0].hunkCount).toBe(2)
      expect(blocks[1].changedLineCount).toBe(2)
      expect(blocks[1].hunkCount).toBe(1)

      const fooAddedImports = blocks[0].lines.filter(line => line.content === "+import React from 'react'")
      expect(fooAddedImports).toHaveLength(1)
      expect(blocks[0].lines.filter(line => line.type === 'hunk')).toHaveLength(2)
    })

    it('keeps metadata-only binary files as file blocks', () => {
      const blocks = parseDiffFiles([
        { file: 'image.png', additions: 0, deletions: 0, diff: '', isBinary: true },
      ])

      expect(blocks).toHaveLength(1)
      expect(blocks[0].newPath).toBe('image.png')
      expect(blocks[0].isBinary).toBe(true)
      expect(blocks[0].hunkCount).toBe(0)
      expect(blocks[0].rawPatch).toBe('')
    })
  })
})
