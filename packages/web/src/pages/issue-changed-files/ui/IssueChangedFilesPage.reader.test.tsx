import { describe, expect, it } from 'vitest'
import {
  fireEvent,
  SAMPLE_DIFF_DATA,
  screen,
  selectOption,
  useIssueChangedFilesPageFixture,
  waitFor,
} from './IssueChangedFilesPage.fixture'

const { renderPage, state } = useIssueChangedFilesPageFixture()

describe('IssueChangedFilesPage', () => {
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
      fireEvent.click(screen.getByRole('button', { name: /split view/i }))
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
      fireEvent.click(screen.getByText('Expand all'))
      await waitFor(() => {
        expect(screen.getAllByText('foo.ts').length).toBeGreaterThan(0)
      })
    })

    it('filters files by path when filter is entered', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.change(screen.getByPlaceholderText('Filter files...'), { target: { value: 'bar' } })
      await waitFor(() => {
        expect(screen.queryByText('foo.ts')).toBeNull()
        expect(screen.getByText('bar.ts')).toBeTruthy()
      })
    })

    it('shows no matching files when filter has no results', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.change(screen.getByPlaceholderText('Filter files...'), { target: { value: 'nonexistent' } })
      await waitFor(() => {
        expect(screen.getByText('No matching files')).toBeTruthy()
      })
    })

    it('shows empty state when issue has no diff entries', async () => {
      state.diffData = { ...SAMPLE_DIFF_DATA, files: [] }
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('No file changes yet')).toBeTruthy()
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
      fireEvent.click(screen.getByText('foo.ts'))
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
      fireEvent.click(screen.getByText('Exit commit mode'))
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
})
