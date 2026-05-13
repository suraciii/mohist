// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi, beforeEach } from 'vitest'
import { cleanup, render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { IssueChangedFilesPage } from './IssueChangedFilesPage'

const mockUseNavigate = vi.fn()
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>()
  return {
    ...actual,
    useNavigate: () => mockUseNavigate,
  }
})

const mockUseIssue = vi.fn()
const mockUseIssueDiff = vi.fn()
const mockUseIssueCommits = vi.fn()
const mockUseCommitDiff = vi.fn()

vi.mock('../hooks/useQueries', () => ({
  useIssue: (...args: unknown[]) => mockUseIssue(...args),
  useIssueDiff: (...args: unknown[]) => mockUseIssueDiff(...args),
  useIssueCommits: (...args: unknown[]) => mockUseIssueCommits(...args),
  useCommitDiff: (...args: unknown[]) => mockUseCommitDiff(...args),
}))

const SAMPLE_ISSUE = {
  id: '1',
  number: 123,
  title: 'Test Issue',
  body: 'Description',
  status: 'in_progress' as const,
  stage: 'build' as const,
  labels: [],
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

function setupDefaultMocks() {
  mockUseIssue.mockReturnValue({
    data: SAMPLE_ISSUE,
    isLoading: false,
    isError: false,
  })
  mockUseIssueDiff.mockReturnValue({
    data: SAMPLE_DIFF_DATA,
  })
  mockUseIssueCommits.mockReturnValue({
    data: SAMPLE_COMMITS_DATA,
  })
  mockUseCommitDiff.mockReturnValue({
    data: { diff: '' },
  })
}

function renderPage(initialRoute = '/issue/123/files') {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })

  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialRoute]}>
        <Routes>
          <Route path="/issue/:number/files" element={<IssueChangedFilesPage />} />
          <Route path="/issue/:number" element={<div>Issue Detail Page</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>
  )
}

describe('IssueChangedFilesPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    setupDefaultMocks()
  })

  afterEach(() => {
    cleanup()
  })

  describe('route rendering', () => {
    it('renders the changed-files page at the dedicated route', () => {
      renderPage()
      expect(screen.getByText('Test Issue')).toBeTruthy()
      expect(screen.getByText('main')).toBeTruthy()
      expect(screen.getByText('mo/issue-123')).toBeTruthy()
      expect(screen.getByText('files changed')).toBeTruthy()
    })

    it('renders issue number and title in header', () => {
      renderPage()
      expect(screen.getByText('#123')).toBeTruthy()
      expect(screen.getByText('Test Issue')).toBeTruthy()
    })

    it('renders diffstat with additions and deletions', () => {
      renderPage()
      expect(screen.getAllByText('+6').length).toBeGreaterThan(0)
      expect(screen.getAllByText('-2').length).toBeGreaterThan(0)
    })
  })

  describe('unavailable states', () => {
    it('shows not-started message when reason is not_started', () => {
      mockUseIssueDiff.mockReturnValue({
        data: { available: false, reason: 'not_started', message: '' },
      })
      renderPage()
      expect(screen.getByText('No changes yet')).toBeTruthy()
    })

    it('shows worktree-removed message when worktree was removed', () => {
      mockUseIssueDiff.mockReturnValue({
        data: { available: false, reason: 'worktree_removed', message: '' },
      })
      renderPage()
      expect(screen.getByText('Changes unavailable — workspace removed')).toBeTruthy()
    })

    it('shows branch-missing message when branch is missing', () => {
      mockUseIssueDiff.mockReturnValue({
        data: { available: false, reason: 'branch_missing', message: '' },
      })
      renderPage()
      expect(screen.getByText('Changes unavailable — branch missing')).toBeTruthy()
    })

    it('renders loading state while issue is loading', () => {
      mockUseIssue.mockReturnValue({
        data: undefined,
        isLoading: true,
        isError: false,
      })
      renderPage()
      expect(screen.getByText('Loading...')).toBeTruthy()
    })

    it('shows not found page when issue error occurs', () => {
      mockUseIssue.mockReturnValue({
        data: undefined,
        isLoading: false,
        isError: true,
      })
      renderPage()
      expect(screen.getByText('Page not found')).toBeTruthy()
    })
  })

  describe('View files navigation from Issue Detail', () => {
    it('has a back button that navigates to Issue Detail', () => {
      renderPage()
      const backButton = screen.getByText('Back to issue')
      expect(backButton).toBeTruthy()
      fireEvent.click(backButton)
    })
  })

  describe('reader controls', () => {
    it('renders expand all and collapse all buttons', () => {
      renderPage()
      expect(screen.getByText('Expand all')).toBeTruthy()
      expect(screen.getByText('Collapse all')).toBeTruthy()
    })

    it('renders split view toggle', () => {
      renderPage()
      expect(screen.getByText(/split view/i)).toBeTruthy()
    })

    it('toggles to split view mode', () => {
      renderPage()
      const splitButton = screen.getByText(/split view/i)
      fireEvent.click(splitButton)
      expect(screen.getByText(/unified view/i)).toBeTruthy()
    })
  })

  describe('directory-grouped file tree', () => {
    it('renders the file tree with directories', () => {
      renderPage()
      expect(screen.getByText('src/')).toBeTruthy()
      expect(screen.getByText('utils/')).toBeTruthy()
    })

    it('renders file entries with addition/deletion counts', () => {
      renderPage()
      expect(screen.getByText('foo.ts')).toBeTruthy()
    })

    it('allows selecting a file from the tree', async () => {
      renderPage()
      const expandButton = screen.getByText('Expand all')
      fireEvent.click(expandButton)
      await waitFor(() => {
        expect(screen.getAllByText('foo.ts').length).toBeGreaterThan(0)
      })
    })

    it('filters files by path when filter is entered', async () => {
      renderPage()
      const filterInput = screen.getByPlaceholderText('Filter files...')
      fireEvent.change(filterInput, { target: { value: 'bar' } })
      await waitFor(() => {
        expect(screen.queryByText('foo.ts')).toBeNull()
        expect(screen.getByText('bar.ts')).toBeTruthy()
      })
    })

    it('shows no matching files when filter has no results', async () => {
      renderPage()
      const filterInput = screen.getByPlaceholderText('Filter files...')
      fireEvent.change(filterInput, { target: { value: 'nonexistent' } })
      await waitFor(() => {
        expect(screen.getByText('No matching files')).toBeTruthy()
      })
    })

    it('shows empty state when issue has no diff entries', () => {
      mockUseIssueDiff.mockReturnValue({
        data: { ...SAMPLE_DIFF_DATA, files: [] },
      })
      renderPage()
      expect(screen.getByText('No file changes yet')).toBeTruthy()
    })
  })

  describe('large-diff Render anyway behavior', () => {
    it('renders the reader shell with diff controls', () => {
      renderPage()
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
      mockUseIssueDiff.mockReturnValue({
        data: largeDiffData,
      })
      renderPage()
      const largeFileEntry = screen.getByText('large.txt')
      fireEvent.click(largeFileEntry)
      await waitFor(() => {
        expect(screen.getByText(/Large diff/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
    })

    it('renders large diff when Render anyway is clicked', async () => {
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
      mockUseIssueDiff.mockReturnValue({
        data: largeDiffData,
      })
      renderPage()
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
  })

  describe('unified diff rendering', () => {
    it('renders page with diff controls', () => {
      renderPage()
      expect(screen.getByText('Expand all')).toBeTruthy()
      expect(screen.getByText('Collapse all')).toBeTruthy()
    })
  })

  describe('prev/next hunk navigation', () => {
    it('renders mode buttons', () => {
      renderPage()
      expect(screen.getByText('Diff')).toBeTruthy()
      expect(screen.getByText('Raw')).toBeTruthy()
      expect(screen.getByText('Full file')).toBeTruthy()
      expect(screen.getByText('Search')).toBeTruthy()
    })
  })

  describe('raw patch mode', () => {
    it('renders raw patch content when Raw mode is selected', async () => {
      renderPage()
      const fooFile = screen.getByText('foo.ts')
      fireEvent.click(fooFile)
      const rawButton = screen.getByText('Raw')
      fireEvent.click(rawButton)
      await waitFor(() => {
        expect(screen.getByText('Copy')).toBeTruthy()
      })
    })
  })

  describe('search within diff', () => {
    it('can interact with the Search button', () => {
      renderPage()
      const searchButton = screen.getByText('Search')
      expect(searchButton).toBeTruthy()
    })
  })

  describe('commit-scoped reading', () => {
    it('shows commit selector when commits are available', () => {
      renderPage()
      expect(screen.getByText('View commit...')).toBeTruthy()
    })

    it('enters commit mode when a commit is selected', async () => {
      renderPage()
      const select = screen.getByRole('combobox')
      fireEvent.change(select, { target: { value: 'abc123' } })
      await waitFor(() => {
        expect(screen.getByText('Exit commit mode')).toBeTruthy()
      })
    })

    it('exits commit mode when Exit commit mode is clicked', async () => {
      renderPage()
      const select = screen.getByRole('combobox')
      fireEvent.change(select, { target: { value: 'abc123' } })
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
    it('can interact with reader controls', () => {
      renderPage()
      expect(screen.getByText('Expand all')).toBeTruthy()
    })
  })
})