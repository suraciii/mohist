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
  describe('unified recoverable error surface', () => {
    it('renders the recovery surface with the product-language message and recovery actions for runner_unavailable', async () => {
      state.diffData = { available: false, reason: 'runner_unavailable', message: 'runner not connected' }
      renderPage()
      await screen.findByTestId('issue-files-recovery-surface')
      expect(screen.getByTestId('issue-files-recovery-message').textContent)
        .toBe('The file changes could not be loaded. The runner may be disconnected.')
      expect(screen.getByTestId('issue-files-recovery-retry')).toBeTruthy()
      expect(screen.getByTestId('issue-files-recovery-return')).toBeTruthy()
    })

    it('renders the recovery surface with the mapped message and actions when commits are unavailable', async () => {
      state.commitsData = { available: false, reason: 'runner_unavailable', message: 'runner not connected' }
      renderPage()
      await screen.findByTestId('issue-files-recovery-surface')
      expect(screen.getByTestId('issue-files-recovery-message').textContent)
        .toBe('The file changes could not be loaded. The runner may be disconnected.')
      expect(screen.getByTestId('issue-files-recovery-retry')).toBeTruthy()
      expect(screen.getByTestId('issue-files-recovery-return')).toBeTruthy()
    })

    it('renders the recovery surface for workspace_removed', async () => {
      state.diffData = { available: false, reason: 'workspace_removed', message: '' }
      renderPage()
      await screen.findByTestId('issue-files-recovery-surface')
      expect(screen.getByTestId('issue-files-recovery-message').textContent)
        .toBe('The file changes could not be loaded. The workspace has been removed.')
    })

    it('renders the recovery surface for branch_missing', async () => {
      state.diffData = { available: false, reason: 'branch_missing', message: '' }
      renderPage()
      await screen.findByTestId('issue-files-recovery-surface')
      expect(screen.getByTestId('issue-files-recovery-message').textContent)
        .toBe('The file changes could not be loaded. The branch could not be found.')
    })

    it('renders the recovery surface for git_error with the dedicated message (not a generic fallback)', async () => {
      state.diffData = { available: false, reason: 'git_error', message: 'fatal: bad object' }
      renderPage()
      await screen.findByTestId('issue-files-recovery-surface')
      expect(screen.getByTestId('issue-files-recovery-message').textContent)
        .toBe('The file changes could not be loaded due to a git error.')
    })

    it('renders the recovery surface for not_started with the dedicated message', async () => {
      state.diffData = { available: false, reason: 'not_started', message: '' }
      renderPage()
      await screen.findByTestId('issue-files-recovery-surface')
      expect(screen.getByTestId('issue-files-recovery-message').textContent)
        .toBe('There are no changes yet.')
    })

    it('does not render the legacy bare orange availability banner', async () => {
      state.diffData = { available: false, reason: 'workspace_removed', message: '' }
      renderPage()
      await screen.findByTestId('issue-files-recovery-surface')
      expect(screen.queryByText('Changes unavailable — workspace removed')).toBeNull()
      expect(screen.queryByText('No changes yet')).toBeNull()
    })
  })

  describe('issue context preservation on the recovery surface', () => {
    it('shows the issue number, title, and health badge when the issue query succeeded', async () => {
      state.diffData = { available: false, reason: 'runner_unavailable', message: '' }
      renderPage()
      const context = await screen.findByTestId('issue-files-recovery-context')
      expect(context.textContent).toContain('#123')
      expect(screen.getByTestId('issue-files-recovery-title').textContent).toBe('Test Issue')
      expect(screen.getByTestId('issue-files-recovery-health')).toBeTruthy()
    })

    it('shows only the issue number when the issue query failed (title/health honestly omitted)', async () => {
      state.issueError = true
      state.issueData = undefined
      renderPage()
      await screen.findByTestId('issue-files-recovery-surface')
      const context = await screen.findByTestId('issue-files-recovery-context')
      expect(context.textContent).toContain('#123')
      expect(screen.queryByTestId('issue-files-recovery-title')).toBeNull()
      expect(screen.queryByTestId('issue-files-recovery-health')).toBeNull()
      expect(screen.getByTestId('issue-files-recovery-message').textContent)
        .toBe('The file changes could not be loaded.')
    })

    it('does not flash the recovery surface during initial load (gated on !isLoading && diffData present)', async () => {
      state.blockIssue = true
      renderPage()
      expect(screen.getByText('Loading...')).toBeTruthy()
      expect(screen.queryByTestId('issue-files-recovery-surface')).toBeNull()
    })
  })

  describe('transport-error path renders the recovery surface', () => {
    it('renders the recovery surface when the issue query fails', async () => {
      state.issueError = true
      state.issueData = undefined
      renderPage()
      await screen.findByTestId('issue-files-recovery-surface')
      expect(screen.getByTestId('issue-files-recovery-retry')).toBeTruthy()
      expect(screen.getByTestId('issue-files-recovery-return')).toBeTruthy()
    })

    it('renders the recovery surface when the diff query fails', async () => {
      state.diffError = true
      state.diffData = undefined
      renderPage()
      await screen.findByTestId('issue-files-recovery-surface')
      expect(screen.getByTestId('issue-files-recovery-message').textContent)
        .toBe('The file changes could not be loaded.')
    })

    it('renders the recovery surface when the commits query fails', async () => {
      state.commitsError = true
      state.commitsData = undefined
      renderPage()
      await screen.findByTestId('issue-files-recovery-surface')
      expect(screen.getByTestId('issue-files-recovery-message').textContent)
        .toBe('The file changes could not be loaded.')
    })

    it('does not render the legacy bare red ErrorState card', async () => {
      state.diffError = true
      state.diffData = undefined
      renderPage()
      await screen.findByTestId('issue-files-recovery-surface')
      expect(screen.queryByText('Failed to load issue diff.')).toBeNull()
      expect(screen.queryByText('Failed to load issue details.')).toBeNull()
      expect(screen.queryByText('Failed to load issue commits.')).toBeNull()
    })
  })

  describe('retry action re-fetches evidence', () => {
    it('invokes refetch on issue, diff, and commits and re-renders the evidence view on success', async () => {
      state.diffData = { available: false, reason: 'runner_unavailable', message: '' }
      renderPage()
      await screen.findByTestId('issue-files-recovery-surface')

      state.diffData = {
        available: true,
        base: 'main',
        head: 'mo/issue-123',
        mergeBase: 'abc123',
        ahead: 1,
        behind: 0,
        canFastForward: false,
        comparison: 'merge-base',
        summary: { filesChanged: 1, additions: 1, deletions: 0 },
        files: [],
      }
      state.commitsData = {
        commits: [{
          hash: 'abc123',
          shortHash: 'abc123',
          message: 'Initial commit',
          author: 'Test User',
          date: '2026-07-11T00:00:00.000Z',
        }],
      }

      fireEvent.click(screen.getByTestId('issue-files-recovery-retry'))

      await waitFor(() => {
        expect(screen.getByText('main')).toBeTruthy()
        expect(screen.getByText('mo/issue-123')).toBeTruthy()
      })
      await waitFor(() => {
        expect(state.issueRequestCount).toBe(2)
        expect(state.diffRequestCount).toBe(2)
        expect(state.commitsRequestCount).toBe(2)
      })
    })

    it('keeps the recovery surface available with the same actions when the failure persists', async () => {
      state.diffError = true
      state.diffData = undefined
      renderPage()
      const surface = await screen.findByTestId('issue-files-recovery-surface')
      expect(screen.getByTestId('issue-files-recovery-retry')).toBeTruthy()
      expect(screen.getByTestId('issue-files-recovery-return')).toBeTruthy()
      await waitFor(() => {
        expect(state.issueRequestCount).toBe(1)
        expect(state.diffRequestCount).toBe(1)
        expect(state.commitsRequestCount).toBe(1)
        expect(state.getIssueQueryFetchStatus()).toBe('idle')
      })
      fireEvent.click(screen.getByTestId('issue-files-recovery-retry'))
      await waitFor(() => {
        expect(state.issueRequestCount).toBe(2)
        expect(state.diffRequestCount).toBe(2)
        expect(state.commitsRequestCount).toBe(2)
      })
      expect(screen.getByTestId('issue-files-recovery-surface')).toBe(surface)
      expect(screen.getByTestId('issue-files-recovery-retry')).toBeTruthy()
      expect(screen.getByTestId('issue-files-recovery-return')).toBeTruthy()
    })
  })

  describe('return-to-issue action', () => {
    it('navigates to the project-scoped issue route from a transport error', async () => {
      state.diffError = true
      state.diffData = undefined
      renderPage()
      await screen.findByTestId('issue-files-recovery-surface')
      fireEvent.click(screen.getByTestId('issue-files-recovery-return'))
      expect(screen.getByTestId('current-path').textContent).toBe('/Test%20Project/issues/123')
    })

    it('navigates to the project-scoped issue route from server-reported unavailability', async () => {
      state.diffData = { available: false, reason: 'workspace_removed', message: '' }
      renderPage()
      await screen.findByTestId('issue-files-recovery-surface')
      fireEvent.click(screen.getByTestId('issue-files-recovery-return'))
      expect(screen.getByTestId('current-path').textContent).toBe('/Test%20Project/issues/123')
    })
  })

  describe('invalid issue number', () => {
    it('renders the InvalidIssueState', () => {
      renderPage('/issues/invalid/files')
      expect(screen.getByText('Invalid issue number')).toBeTruthy()
    })
  })

  describe('commit-diff error remains a reader-pane recovery', () => {
    it('shows exit-commit-mode and back-to-issue controls for a per-commit diff failure', async () => {
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
})
