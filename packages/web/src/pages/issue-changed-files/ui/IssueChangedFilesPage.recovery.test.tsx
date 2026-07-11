import { describe, expect, it } from 'vitest'
import {
  fireEvent,
  screen,
  selectOption,
  useIssueChangedFilesPageFixture,
  waitFor,
} from './IssueChangedFilesPage.fixture'

const { renderPage, state } = useIssueChangedFilesPageFixture()

describe('IssueChangedFilesPage', () => {
  describe('unavailable states', () => {
    it('shows not-started message when reason is not_started', async () => {
      state.diffData = { available: false, reason: 'not_started', message: '' }
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('No changes yet')).toBeTruthy()
    })

    it('shows workspace-removed message when workspace was removed', async () => {
      state.diffData = { available: false, reason: 'workspace_removed', message: '' }
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('Changes unavailable — workspace removed')).toBeTruthy()
    })

    it('shows branch-missing message when branch is missing', async () => {
      state.diffData = { available: false, reason: 'branch_missing', message: '' }
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('Changes unavailable — branch missing')).toBeTruthy()
    })

    it('renders loading state while issue is loading', () => {
      state.blockIssue = true
      renderPage()
      expect(screen.getByText('Loading...')).toBeTruthy()
    })

    it('shows recoverable error when issue API fails', async () => {
      state.issueError = true
      state.issueData = undefined
      renderPage()
      await waitFor(() => {
        expect(screen.getByText('Failed to load issue details.')).toBeTruthy()
      })
      expect(screen.getByText('View issue detail')).toBeTruthy()
    })

    it('shows recoverable error when diff API fails', async () => {
      state.diffError = true
      state.diffData = undefined
      renderPage()
      await waitFor(() => {
        expect(screen.getByText('Failed to load issue diff.')).toBeTruthy()
      })
      expect(screen.getByText('View issue detail')).toBeTruthy()
    })

    it('shows recoverable error when commits API fails', async () => {
      state.commitsError = true
      state.commitsData = undefined
      renderPage()
      await waitFor(() => {
        expect(screen.getByText('Failed to load issue commits.')).toBeTruthy()
      })
      expect(screen.getByText('View issue detail')).toBeTruthy()
    })

    it('shows recoverable error when commit diff API fails', async () => {
      state.commitDiffError = true
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

  describe('recoverable error UI', () => {
    it('renders visible error state when issue API fails', async () => {
      state.issueError = true
      state.issueData = undefined
      renderPage()
      await waitFor(() => {
        expect(screen.getByText('Failed to load issue details.')).toBeTruthy()
      })
      expect(screen.getByText('View issue detail')).toBeTruthy()
    })

    it('renders visible error state when diff API fails', async () => {
      state.diffError = true
      state.diffData = undefined
      renderPage()
      await waitFor(() => {
        expect(screen.getByText('Failed to load issue diff.')).toBeTruthy()
      })
      expect(screen.getByText('View issue detail')).toBeTruthy()
    })

    it('renders visible error state when commits API fails', async () => {
      state.commitsError = true
      state.commitsData = undefined
      renderPage()
      await waitFor(() => {
        expect(screen.getByText('Failed to load issue commits.')).toBeTruthy()
      })
      expect(screen.getByText('View issue detail')).toBeTruthy()
    })

    it('shows error with path back to issue detail page', async () => {
      state.issueError = true
      state.issueData = undefined
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
      renderPage('/issues/invalid/files')
      expect(screen.getByText('Invalid issue number')).toBeTruthy()
    })

    it('renders recoverable error for unavailable diff with reason', async () => {
      state.diffData = { available: false, reason: 'workspace_removed', message: 'workspace removed' }
      renderPage()
      await screen.findByText('Test Issue')
      expect(screen.getByText('Changes unavailable — workspace removed')).toBeTruthy()
    })
  })
})
