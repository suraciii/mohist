import { describe, expect, it } from 'vitest'
import {
  changedFile,
  fireEvent,
  makeLargeDiff,
  screen,
  selectOption,
  useIssueChangedFilesPageFixture,
  waitFor,
  withFiles,
} from './IssueChangedFilesPage.fixture'

const { renderPage, state } = useIssueChangedFilesPageFixture()

describe('IssueChangedFilesPage', () => {
  describe('large-diff Render anyway behavior', () => {
    it('renders the reader shell with diff controls', async () => {
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('Expand all')).toBeTruthy()
    })

    it('shows large diff placeholder when file exceeds threshold', async () => {
      state.diffData = withFiles([changedFile('src/large.txt', 175, 175, makeLargeDiff())])
      renderPage()
      await screen.findByText('Test Issue')
      fireEvent.click(screen.getByText('large.txt'))
      await waitFor(() => {
        expect(screen.getByText(/Large diff/)).toBeTruthy()
        expect(screen.getByText(/Render anyway/)).toBeTruthy()
      })
    })

    it('renders large diff when Render anyway is clicked', async () => {
      state.diffData = withFiles([changedFile('src/large.txt', 175, 175, makeLargeDiff())])
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
    })

    it('keeps Render anyway active for the selected file across reader modes', async () => {
      state.diffData = withFiles([changedFile('src/large.txt', 175, 175, makeLargeDiff())])
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
        expect(state.fileContentHandler).toHaveBeenCalled()
      })
    })
  })
})
