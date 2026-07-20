import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { IssueStatus, IssueHealth } from '../../../entities/issue'
import { makeIssue, mockAgentStatus, renderBoard } from './_kanbanBoardQueryTestUtils'

import { KanbanBoard } from './KanbanBoard'

let previousUrl = ''
let previousHistoryState: unknown

beforeEach(() => {
  previousUrl = window.location.href
  previousHistoryState = window.history.state
  window.history.replaceState(null, '', '/')
})

afterEach(() => {
  cleanup()
  window.history.replaceState(previousHistoryState, '', previousUrl)
  vi.clearAllMocks()
})

function getDesktopFilterBar(): HTMLElement {
  const filterBar = document.querySelector<HTMLElement>(
    '.hidden.md\\:flex.flex-wrap',
  )
  if (!filterBar) {
    throw new Error('Desktop FilterBar (hidden md:flex flex-wrap) not found')
  }
  return filterBar
}

describe('KanbanBoard - repository filter reachability', () => {
  describe('Desktop filter bar exposes the repository selector', () => {
    it('renders the repository filter inside the desktop filter bar when repositories exist', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog, repositoryName: 'web' }),
        makeIssue({ number: 2, status: IssueStatus.InProgress, repositoryName: 'server' }),
      ]
      renderBoard(<KanbanBoard issues={issues} agentStatus={mockAgentStatus} />)

      const filterBar = getDesktopFilterBar()
      const repoFilter = within(filterBar).getByTestId('repository-filter')
      expect(repoFilter).toBeInTheDocument()
      expect(repoFilter.tagName).toBe('SELECT')
    })

    it('lists every persisted repository as an option and defaults to "All repositories"', () => {
      const issues = [
        makeIssue({ number: 1, repositoryName: 'web' }),
        makeIssue({ number: 2, repositoryName: 'server' }),
        makeIssue({ number: 3, repositoryName: 'docs' }),
      ]
      renderBoard(<KanbanBoard issues={issues} agentStatus={mockAgentStatus} />)

      const repoFilter = within(getDesktopFilterBar()).getByTestId<HTMLSelectElement>('repository-filter')
      const optionValues = Array.from(repoFilter.options).map((o) => o.value)
      expect(optionValues).toContain('')
      expect(optionValues).toContain('web')
      expect(optionValues).toContain('server')
      expect(optionValues).toContain('docs')
      expect(repoFilter.value).toBe('')
    })

    it('lists declared repositories even when no issue is assigned to one yet', () => {
      const issues = [makeIssue({ number: 1, repositoryName: 'web' })]
      renderBoard(
        <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />,
        [
          { name: 'web', gitUrl: 'git@x:web.git', baseBranch: 'main', isDefault: true },
          { name: 'infra', gitUrl: 'git@x:infra.git', baseBranch: 'main', isDefault: false },
        ],
      )

      const repoFilter = within(getDesktopFilterBar()).getByTestId<HTMLSelectElement>('repository-filter')
      expect(Array.from(repoFilter.options, (option) => option.value)).toEqual(['', 'infra', 'web'])

      fireEvent.change(repoFilter, { target: { value: 'infra' } })
      expect(screen.queryByText('#1')).not.toBeInTheDocument()
    })

    it('hides the repository filter when no issues carry a persisted repository', () => {
      const issues = [
        makeIssue({ number: 1, repository: null, repositoryName: null }),
      ]
      renderBoard(<KanbanBoard issues={issues} agentStatus={mockAgentStatus} />)

      const filterBar = getDesktopFilterBar()
      expect(within(filterBar).queryByTestId('repository-filter')).not.toBeInTheDocument()
    })
  })

  describe('Mobile filter panel exposes the repository selector behind the existing disclosure', () => {
    it('renders the mobile repository filter inside the mobile filter panel once the toggle is opened', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog, repositoryName: 'web' }),
        makeIssue({ number: 2, status: IssueStatus.InProgress, repositoryName: 'server' }),
      ]
      renderBoard(<KanbanBoard issues={issues} agentStatus={mockAgentStatus} />)

      expect(screen.queryByTestId('mobile-repository-filter')).not.toBeInTheDocument()

      fireEvent.click(screen.getByTestId('mobile-filter-toggle'))

      const panel = screen.getByTestId('mobile-filter-panel')
      const mobileRepoFilter = within(panel).getByTestId('mobile-repository-filter')
      expect(mobileRepoFilter).toBeInTheDocument()
      expect(mobileRepoFilter.tagName).toBe('SELECT')
    })

    it('hides the mobile repository filter when no issues carry a persisted repository', () => {
      const issues = [
        makeIssue({ number: 1, repository: null, repositoryName: null }),
      ]
      renderBoard(<KanbanBoard issues={issues} agentStatus={mockAgentStatus} />)

      fireEvent.click(screen.getByTestId('mobile-filter-toggle'))

      const panel = screen.getByTestId('mobile-filter-panel')
      expect(within(panel).queryByTestId('mobile-repository-filter')).not.toBeInTheDocument()
    })
  })

  describe('Repository filter behavior', () => {
    it('filters every status column by exact persisted repository assignment', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog, repositoryName: 'web' }),
        makeIssue({ number: 2, status: IssueStatus.Backlog, repositoryName: 'server' }),
        makeIssue({ number: 3, status: IssueStatus.InProgress, repositoryName: 'web' }),
        makeIssue({ number: 4, status: IssueStatus.Done, repositoryName: 'server' }),
      ]
      renderBoard(<KanbanBoard issues={issues} agentStatus={mockAgentStatus} />)

      const repoFilter = within(getDesktopFilterBar()).getByTestId<HTMLSelectElement>('repository-filter')
      fireEvent.change(repoFilter, { target: { value: 'web' } })

      const desktopBoard = document.querySelector('.hidden.md\\:flex.flex-row') as HTMLElement
      expect(desktopBoard).toBeInTheDocument()
      const backlogColumn = desktopBoard.querySelector('[data-testid="stage-column-backlog"]') as HTMLElement
      const inProgressColumn = desktopBoard.querySelector('[data-testid="stage-column-in_progress"]') as HTMLElement
      const doneColumn = desktopBoard.querySelector('[data-testid="stage-column-done"]') as HTMLElement

      expect(within(backlogColumn).queryByText('#1')).toBeInTheDocument()
      expect(within(backlogColumn).queryByText('#2')).not.toBeInTheDocument()

      expect(within(inProgressColumn).queryByText('#3')).toBeInTheDocument()
      expect(within(inProgressColumn).queryByText('#4')).not.toBeInTheDocument()

      expect(within(doneColumn).queryByText('#4')).not.toBeInTheDocument()
    })

    it('writes the repository filter into the URL query string', () => {
      const issues = [
        makeIssue({ number: 1, repositoryName: 'web' }),
        makeIssue({ number: 2, repositoryName: 'server' }),
      ]
      renderBoard(<KanbanBoard issues={issues} agentStatus={mockAgentStatus} />)

      const repoFilter = within(getDesktopFilterBar()).getByTestId<HTMLSelectElement>('repository-filter')
      fireEvent.change(repoFilter, { target: { value: 'web' } })

      expect(window.location.search).toContain('repository=web')
    })

    it('restores the repository filter from the URL on mount', () => {
      window.history.replaceState(null, '', '/?repository=web')

      const issues = [
        makeIssue({ number: 1, repositoryName: 'web' }),
        makeIssue({ number: 2, repositoryName: 'server' }),
      ]
      renderBoard(<KanbanBoard issues={issues} agentStatus={mockAgentStatus} />)

      const desktopBoard = document.querySelector('.hidden.md\\:flex.flex-row') as HTMLElement
      const backlogColumn = desktopBoard.querySelector('[data-testid="stage-column-backlog"]') as HTMLElement
      expect(within(backlogColumn).queryByText('#1')).toBeInTheDocument()
      expect(within(backlogColumn).queryByText('#2')).not.toBeInTheDocument()

      const repoFilter = within(getDesktopFilterBar()).getByTestId<HTMLSelectElement>('repository-filter')
      expect(repoFilter.value).toBe('web')
    })

    it('yields zero matches for an unknown repository URL value and the user can clear it', async () => {
      window.history.replaceState(null, '', '/?repository=does-not-exist')

      const issues = [
        makeIssue({ number: 1, repositoryName: 'web' }),
        makeIssue({ number: 2, repositoryName: 'server' }),
      ]
      renderBoard(<KanbanBoard issues={issues} agentStatus={mockAgentStatus} />)

      const desktopBoard = document.querySelector('.hidden.md\\:flex.flex-row') as HTMLElement
      const backlogColumn = desktopBoard.querySelector('[data-testid="stage-column-backlog"]') as HTMLElement
      expect(within(backlogColumn).queryByText('#1')).not.toBeInTheDocument()
      expect(within(backlogColumn).queryByText('#2')).not.toBeInTheDocument()

      const repoFilter = within(getDesktopFilterBar()).getByTestId<HTMLSelectElement>('repository-filter')
      expect(repoFilter.value).toBe('does-not-exist')

      fireEvent.click(within(getDesktopFilterBar()).getByTestId('repository-filter-clear'))

      await waitFor(() => {
        expect(within(backlogColumn).queryByText('#1')).toBeInTheDocument()
        expect(within(backlogColumn).queryByText('#2')).toBeInTheDocument()
      })
      expect(repoFilter.value).toBe('')
    })

    it('keeps priority, labels, search, and sort intact when only the repository filter is cleared', () => {
      window.history.replaceState(
        null,
        '',
        '/?repository=web&priorities=p1&labels=stream%3Dapi&search=login&sort=updated',
      )

      const issues = [
        makeIssue({ number: 1, title: 'Login flow', priority: 'p1', repositoryName: 'web', labels: { stream: 'api' } }),
        makeIssue({ number: 2, title: 'Other flow', priority: 'p1', repositoryName: 'server', labels: { stream: 'api' } }),
      ]
      renderBoard(<KanbanBoard issues={issues} agentStatus={mockAgentStatus} />)

      const filterBar = getDesktopFilterBar()
      const repoFilter = within(filterBar).getByTestId<HTMLSelectElement>('repository-filter')
      expect(repoFilter.value).toBe('web')

      fireEvent.click(within(filterBar).getByTestId('repository-filter-clear'))

      const params = new URLSearchParams(window.location.search)
      expect(params.get('repository')).toBeNull()
      expect(params.get('priorities')).toBe('p1')
      expect(params.getAll('labels')).toContain('stream=api')
      expect(params.get('search')).toBe('login')
      expect(params.get('sort')).toBe('updated')
    })

    it('composes the repository filter with the existing priority filter', () => {
      const issues = [
        makeIssue({ number: 1, priority: 'p0', repositoryName: 'web' }),
        makeIssue({ number: 2, priority: 'p1', repositoryName: 'web' }),
        makeIssue({ number: 3, priority: 'p0', repositoryName: 'server' }),
      ]
      renderBoard(<KanbanBoard issues={issues} agentStatus={mockAgentStatus} />)

      const filterBar = getDesktopFilterBar()
      fireEvent.click(within(filterBar).getByTestId('priority-chip-p1'))

      const repoFilter = within(filterBar).getByTestId<HTMLSelectElement>('repository-filter')
      fireEvent.change(repoFilter, { target: { value: 'web' } })

      const desktopBoard = document.querySelector('.hidden.md\\:flex.flex-row') as HTMLElement
      const backlogColumn = desktopBoard.querySelector('[data-testid="stage-column-backlog"]') as HTMLElement
      expect(within(backlogColumn).queryByText('#2')).toBeInTheDocument()
      expect(within(backlogColumn).queryByText('#1')).not.toBeInTheDocument()
      expect(within(backlogColumn).queryByText('#3')).not.toBeInTheDocument()
    })

    it('does not drop cancelled cards from the cancelled stub when a repository filter narrows the board', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Cancelled, health: IssueHealth.Cancelled, repositoryName: 'web' }),
        makeIssue({ number: 2, status: IssueStatus.Cancelled, health: IssueHealth.Cancelled, repositoryName: 'server' }),
      ]
      renderBoard(<KanbanBoard issues={issues} agentStatus={mockAgentStatus} />)

      const filterBar = getDesktopFilterBar()
      const repoFilter = within(filterBar).getByTestId<HTMLSelectElement>('repository-filter')
      fireEvent.change(repoFilter, { target: { value: 'web' } })

      const stub = screen.getByTestId('cancelled-collapsed-stub')
      expect(stub.textContent).toContain('1')
    })

    it('renders the desktop archive controls in the Done column footer regardless of the repository filter', () => {
      const issues = Array.from({ length: 3 }, (_, i) =>
        makeIssue({
          number: 400 + i + 1,
          title: `Done issue ${i + 1}`,
          status: IssueStatus.Done,
          health: IssueHealth.Done,
          repositoryName: 'web',
        }),
      )
      renderBoard(<KanbanBoard issues={issues} agentStatus={mockAgentStatus} archivedCount={5} />)

      const filterBar = getDesktopFilterBar()
      const repoFilter = within(filterBar).getByTestId<HTMLSelectElement>('repository-filter')
      fireEvent.change(repoFilter, { target: { value: 'web' } })

      const doneColumn = screen.getByTestId('stage-column-done')
      expect(within(doneColumn).getByText('Archive all done')).toBeInTheDocument()
      expect(within(doneColumn).getByText(/5 archived/)).toBeInTheDocument()
    })

    it('survives browser back/forward navigation through popstate', async () => {
      const issues = [
        makeIssue({ number: 1, repositoryName: 'web' }),
        makeIssue({ number: 2, repositoryName: 'server' }),
      ]
      renderBoard(<KanbanBoard issues={issues} agentStatus={mockAgentStatus} />)

      const filterBar = getDesktopFilterBar()
      const repoFilter = within(filterBar).getByTestId<HTMLSelectElement>('repository-filter')

      fireEvent.change(repoFilter, { target: { value: 'web' } })
      expect(window.location.search).toContain('repository=web')

      window.history.replaceState(null, '', '/?repository=server')
      act(() => {
        window.dispatchEvent(new PopStateEvent('popstate'))
      })

      await waitFor(() => {
        expect(repoFilter.value).toBe('server')
      })

      const desktopBoard = document.querySelector('.hidden.md\\:flex.flex-row') as HTMLElement
      const backlogColumn = desktopBoard.querySelector('[data-testid="stage-column-backlog"]') as HTMLElement
      expect(within(backlogColumn).queryByText('#2')).toBeInTheDocument()
      expect(within(backlogColumn).queryByText('#1')).not.toBeInTheDocument()
    })
  })

  describe('Single-repository projects', () => {
    it('expose the only repository as the sole option and still render the chip on every card', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog, repositoryName: 'main' }),
        makeIssue({ number: 2, status: IssueStatus.InProgress, repositoryName: 'main' }),
      ]
      renderBoard(<KanbanBoard issues={issues} agentStatus={mockAgentStatus} />)

      const repoFilter = within(getDesktopFilterBar()).getByTestId<HTMLSelectElement>('repository-filter')
      const optionValues = Array.from(repoFilter.options).map((o) => o.value)
      expect(optionValues).toContain('')
      expect(optionValues).toContain('main')

      const desktopBoard = document.querySelector('.hidden.md\\:flex.flex-row') as HTMLElement
      const backlogColumn = desktopBoard.querySelector('[data-testid="stage-column-backlog"]') as HTMLElement
      const inProgressColumn = desktopBoard.querySelector('[data-testid="stage-column-in_progress"]') as HTMLElement

      expect(within(backlogColumn).getByTestId('issue-card-repository')).toHaveTextContent('main')
      expect(within(inProgressColumn).getByTestId('issue-card-repository')).toHaveTextContent('main')
    })
  })
})
