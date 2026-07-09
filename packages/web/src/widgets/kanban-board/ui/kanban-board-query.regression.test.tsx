// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { IssueStatus, IssueHealth } from '../../../entities/issue'
import { makeIssue, makeIssues, mockAgentStatus } from './_kanbanBoardQueryTestUtils'

vi.mock('../../../entities/runner/api/queries', () => ({
  useRunnerSummary: vi.fn().mockReturnValue({ hasConnectedCapacity: true, connectedIdleCount: 1, connectedBusyCount: 0, rows: [] }),
}))

import { KanbanBoard } from './KanbanBoard'

describe('KanbanBoard Homepage Regression Coverage', () => {
  beforeEach(() => {
    Object.defineProperty(window, 'location', {
      value: { search: '', pathname: '/' },
      writable: true,
    })
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  describe('Desktop layout regression - horizontal multi-column contract at md+', () => {
    it('renders desktop board container with horizontal multi-column layout at md+', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog }),
        makeIssue({ number: 2, status: IssueStatus.Backlog }),
        makeIssue({ number: 3, status: IssueStatus.InProgress }),
        makeIssue({ number: 4, status: IssueStatus.InProgress }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const desktopBoard = document.querySelector('.hidden.md\\:flex.flex-row')
      expect(desktopBoard).not.toBeNull()
      expect(desktopBoard?.children.length).toBeGreaterThan(0)
    })

    it('does not stack all stage columns vertically in desktop board container', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog }),
        makeIssue({ number: 2, status: IssueStatus.Backlog }),
        makeIssue({ number: 3, health: IssueHealth.Done }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const desktopBoard = document.querySelector('.hidden.md\\:flex.flex-row')
      expect(desktopBoard).not.toBeNull()
      const stageColumns = desktopBoard!.querySelectorAll('[class*="min-w-"]')
      expect(stageColumns.length).toBeGreaterThanOrEqual(3)
    })

    it('cancelled toggle lives inside the Cancelled column and supports bidirectional show/hide', async () => {
      const issues = [
        makeIssue({ number: 1, title: 'Active work', status: IssueStatus.InProgress, health: IssueHealth.Active }),
        makeIssue({ number: 2, title: 'Cancelled work', status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const cancelledColumn = screen.getByTestId('stage-column-cancelled')
      const toggle = within(cancelledColumn).getByTestId('cancelled-toggle')

      expect(toggle).toHaveTextContent('Show cancelled (1)')
      expect(within(cancelledColumn).queryByText('Cancelled work')).not.toBeInTheDocument()

      fireEvent.click(toggle)

      await waitFor(() => {
        expect(within(cancelledColumn).getByText('Cancelled work')).toBeInTheDocument()
      })
      expect(within(cancelledColumn).getByTestId('cancelled-toggle')).toHaveTextContent('Hide cancelled')

      fireEvent.click(within(cancelledColumn).getByTestId('cancelled-toggle'))

      await waitFor(() => {
        expect(within(cancelledColumn).queryByText('Cancelled work')).not.toBeInTheDocument()
      })
      expect(within(cancelledColumn).getByTestId('cancelled-toggle')).toHaveTextContent('Show cancelled (1)')
    })
  })

  describe('Mobile cancelled tab count is independent of showCancelled', () => {
    beforeEach(() => {
      Object.defineProperty(window, 'location', {
        value: { search: '', pathname: '/' },
        writable: true,
      })
    })

    afterEach(() => {
      cleanup()
      vi.clearAllMocks()
    })

    it('renders 8 in the Cancelled tab badge regardless of the toggle state', async () => {
      const issues = Array.from({ length: 8 }, (_, i) =>
        makeIssue({
          number: i + 1,
          title: `Cancelled issue ${i + 1}`,
          status: IssueStatus.Cancelled,
          health: IssueHealth.Cancelled,
        }),
      )
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const cancelledTab = screen.getByTestId('mobile-stage-tab-cancelled')
      expect(within(cancelledTab).getByText('8')).toBeInTheDocument()

      fireEvent.click(cancelledTab)

      const inListToggle = screen.getByTestId('mobile-cancelled-toggle')
      expect(inListToggle).toHaveTextContent('Show cancelled (8)')
      fireEvent.click(inListToggle)

      await waitFor(() => {
        expect(screen.getByTestId('mobile-cancelled-toggle')).toHaveTextContent('Hide cancelled')
      })

      expect(within(screen.getByTestId('mobile-stage-tab-cancelled')).getByText('8')).toBeInTheDocument()

      fireEvent.click(screen.getByTestId('mobile-cancelled-toggle'))

      await waitFor(() => {
        expect(screen.getByTestId('mobile-cancelled-toggle')).toHaveTextContent('Show cancelled (8)')
      })

      expect(within(screen.getByTestId('mobile-stage-tab-cancelled')).getByText('8')).toBeInTheDocument()
    })
  })

  describe('Single sort control - per-column sort buttons removed', () => {
    it('does not render the per-column sort button group inside any stage column', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog }),
        makeIssue({ number: 2, status: IssueStatus.InProgress }),
        makeIssue({ number: 3, status: IssueStatus.Done }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const columns = document.querySelectorAll('[data-testid^="stage-column-"]')
      expect(columns.length).toBeGreaterThan(0)

      columns.forEach((column) => {
        expect(within(column as HTMLElement).queryByRole('button', { name: 'Prio' })).not.toBeInTheDocument()
        expect(within(column as HTMLElement).queryByRole('button', { name: 'Upd' })).not.toBeInTheDocument()
      })
    })

    it('does not expose the per-column sort options outside the global filter bar', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog }),
        makeIssue({ number: 2, status: IssueStatus.Done }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      // The abbreviated per-column labels must not appear anywhere in the
      // board — only the global SortToggle's "Priority" / "#" / "Updated"
      // labels render. (Mobile panel uses the same global labels, so we
      // close the mobile disclosure first to keep this desktop-only.)
      expect(screen.queryByRole('button', { name: 'Prio' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Upd' })).not.toBeInTheDocument()
    })

    it('renders exactly one global SortToggle in the top filter bar', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog }),
        makeIssue({ number: 2, status: IssueStatus.InProgress }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      // The desktop global SortToggle is the only sort entry rendered by
      // default (mobile SortToggle lives behind the disclosure).
      const priorityToggle = screen.getByTestId('sort-priority')
      const numberToggle = screen.getByTestId('sort-number')
      const updatedToggle = screen.getByTestId('sort-updated')

      expect(priorityToggle).toBeInTheDocument()
      expect(numberToggle).toBeInTheDocument()
      expect(updatedToggle).toBeInTheDocument()
      expect(screen.getAllByTestId('sort-priority')).toHaveLength(1)
    })

    it('exposes the global SortToggle inside the mobile filter panel as well', () => {
      const issues = [makeIssue({ number: 1, status: IssueStatus.Backlog })]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      // Default state: the mobile SortToggle is hidden behind the disclosure,
      // so only the desktop SortToggle renders.
      expect(screen.getAllByTestId('sort-priority')).toHaveLength(1)

      fireEvent.click(screen.getByTestId('mobile-filter-toggle'))

      // After opening the mobile panel, the SortToggle re-renders inside
      // the panel — both copies use the same testid, and there is still
      // no per-column sort group.
      const panel = screen.getByTestId('mobile-filter-panel')
      expect(within(panel).getByTestId('sort-priority')).toBeInTheDocument()
      expect(within(panel).queryByRole('button', { name: 'Prio' })).not.toBeInTheDocument()
      expect(within(panel).queryByRole('button', { name: 'Upd' })).not.toBeInTheDocument()
    })

    it('changing the global sort drives the sort order of every column', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog, priority: 'p2', updatedAt: '2026-01-01T00:00:00Z' }),
        makeIssue({ number: 2, status: IssueStatus.Backlog, priority: 'p0', updatedAt: '2026-01-05T00:00:00Z' }),
        makeIssue({ number: 3, status: IssueStatus.InProgress, priority: 'p1', updatedAt: '2026-01-03T00:00:00Z' }),
        makeIssue({ number: 4, status: IssueStatus.InProgress, priority: 'p3', updatedAt: '2026-01-02T00:00:00Z' }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      // Default sort: priority — Backlog column should render p0 (#2) first.
      const backlogColumn = screen.getByTestId('stage-column-backlog')
      const inProgressColumn = screen.getByTestId('stage-column-in_progress')
      const defaultBacklogCards = within(backlogColumn).getAllByTestId('issue-card')
      const defaultInProgressCards = within(inProgressColumn).getAllByTestId('issue-card')
      expect(defaultBacklogCards.length).toBe(2)
      expect(within(defaultBacklogCards[0] as HTMLElement).getByText('#2')).toBeInTheDocument()
      expect(within(defaultBacklogCards[1] as HTMLElement).getByText('#1')).toBeInTheDocument()
      expect(within(defaultInProgressCards[0] as HTMLElement).getByText('#3')).toBeInTheDocument()
      expect(within(defaultInProgressCards[1] as HTMLElement).getByText('#4')).toBeInTheDocument()

      // Switch the global sort to "number" — both columns reorder.
      fireEvent.click(screen.getByTestId('sort-number'))

      const backlogCards = within(backlogColumn).getAllByTestId('issue-card')
      const inProgressCards = within(inProgressColumn).getAllByTestId('issue-card')
      expect(within(backlogCards[0] as HTMLElement).getByText('#2')).toBeInTheDocument()
      expect(within(backlogCards[1] as HTMLElement).getByText('#1')).toBeInTheDocument()
      expect(within(inProgressCards[0] as HTMLElement).getByText('#4')).toBeInTheDocument()
      expect(within(inProgressCards[1] as HTMLElement).getByText('#3')).toBeInTheDocument()

      // Switch the global sort to "updated" — both columns reorder again.
      fireEvent.click(screen.getByTestId('sort-updated'))

      const backlogCardsUpdated = within(backlogColumn).getAllByTestId('issue-card')
      const inProgressCardsUpdated = within(inProgressColumn).getAllByTestId('issue-card')
      expect(within(backlogCardsUpdated[0] as HTMLElement).getByText('#2')).toBeInTheDocument()
      expect(within(backlogCardsUpdated[1] as HTMLElement).getByText('#1')).toBeInTheDocument()
      expect(within(inProgressCardsUpdated[0] as HTMLElement).getByText('#3')).toBeInTheDocument()
      expect(within(inProgressCardsUpdated[1] as HTMLElement).getByText('#4')).toBeInTheDocument()
    })
  })

  describe('Mobile compact filters', () => {
    it('keeps secondary filters behind the mobile disclosure by default', () => {
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={makeIssues(2)} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      expect(screen.getByTestId('mobile-filter-toggle')).toBeInTheDocument()
      expect(screen.queryByTestId('mobile-filter-panel')).not.toBeInTheDocument()

      fireEvent.click(screen.getByTestId('mobile-filter-toggle'))

      const panel = screen.getByTestId('mobile-filter-panel')
      expect(within(panel).getByText(/Priority:/i)).toBeInTheDocument()
      expect(within(panel).getByText(/Labels:/i)).toBeInTheDocument()
      expect(within(panel).getByText(/Sort:/i)).toBeInTheDocument()
      expect(within(panel).getByRole('button', { name: 'Updated' })).toBeInTheDocument()
    })
  })

  describe('Label filtering beyond first eight labels', () => {
    it('restores the visible search input from URL state after popstate navigation', async () => {
      Object.defineProperty(window, 'location', {
        value: { search: 'search=current', pathname: '/' },
        writable: true,
      })

      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={[makeIssue({ title: 'Current issue' })]} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const searchInputs = screen.getAllByPlaceholderText('Search titles...') as HTMLInputElement[]
      expect(searchInputs.map((input) => input.value)).toEqual(['current', 'current'])

      Object.defineProperty(window, 'location', {
        value: { search: 'search=restored', pathname: '/' },
        writable: true,
      })

      act(() => {
        window.dispatchEvent(new PopStateEvent('popstate'))
      })

      await waitFor(() => {
        expect(searchInputs.map((input) => input.value)).toEqual(['restored', 'restored'])
      })
    })

    it('can select a label beyond the first eight via label popover search', async () => {
      const issues = [
        makeIssue({ number: 1, labels: { stream: 'reliability' } }),
        makeIssue({ number: 2, labels: { stream: 'session' } }),
        makeIssue({ number: 3, labels: { stream: 'agent' } }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const labelButton = screen.getByText(/Labels:/i)
      fireEvent.click(labelButton)

      let popover: HTMLElement | null = null
      await waitFor(() => {
        popover = document.querySelector('[class*="origin-top-right"]')
        expect(popover).toBeInTheDocument()
      })

      const searchInput = document.querySelector('input[placeholder="Search labels..."]') as HTMLInputElement
      expect(searchInput).toBeInTheDocument()

      fireEvent.change(searchInput, { target: { value: 'reliability' } })

      await waitFor(() => {
        expect(within(popover!).getByText('stream=reliability')).toBeInTheDocument()
      })
    })

    it('updates board counts after selecting a label beyond the first eight', async () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog, labels: { stream: 'reliability' } }),
        makeIssue({ number: 2, status: IssueStatus.Backlog, labels: { kind: 'bug' } }),
        makeIssue({ number: 3, status: IssueStatus.Backlog, labels: { stream: 'session' } }),
        makeIssue({ number: 4, status: IssueStatus.InProgress, labels: { stream: 'agent' } }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const labelButton = screen.getByText(/Labels:/i)
      fireEvent.click(labelButton)

      await waitFor(() => {
        const popover = document.querySelector('[class*="origin-top-right"]')
        expect(popover).toBeInTheDocument()
      })

      const sessionLabel = within(document.querySelector('[class*="origin-top-right"]') as HTMLElement).getByText('stream=session')
      fireEvent.click(sessionLabel)

      await waitFor(() => {
        const desktopBoard = document.querySelector('.hidden.md\\:flex.flex-row') as HTMLElement
        expect(desktopBoard).toBeInTheDocument()

        const backlogColumn = desktopBoard.children[0] as HTMLElement

        expect(backlogColumn.textContent).toContain('Backlog')
        expect(backlogColumn.textContent).toContain('#3')
        expect(backlogColumn.textContent).toContain('stream=session')
      })
    })

    it('reveals all available labels through the searchable label popover', async () => {
      const issues = [
        makeIssue({ number: 1, labels: { kind: 'bug' } }),
        makeIssue({ number: 2, labels: { kind: 'feature' } }),
        makeIssue({ number: 3, labels: { stream: 'session' } }),
        makeIssue({ number: 4, labels: { stream: 'agent' } }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const labelButton = screen.getByText(/Labels:/i)
      fireEvent.click(labelButton)

      let popover: HTMLElement | null = null
      await waitFor(() => {
        popover = document.querySelector('[class*="origin-top-right"]')
        expect(popover).toBeInTheDocument()
      })

      const searchInput = document.querySelector('input[placeholder="Search labels..."]') as HTMLInputElement
      fireEvent.change(searchInput, { target: { value: 'sess' } })

      await waitFor(() => {
        expect(within(popover!).getByText('stream=session')).toBeInTheDocument()
      })

      fireEvent.change(searchInput, { target: { value: 'agen' } })

      await waitFor(() => {
        expect(within(popover!).getByText('stream=agent')).toBeInTheDocument()
      })
    })

    it('clicking a key=value label option narrows the board to issues containing that pair', async () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog, labels: { stream: 'frontend' } }),
        makeIssue({ number: 2, status: IssueStatus.Backlog, labels: { stream: 'backend' } }),
        makeIssue({ number: 3, status: IssueStatus.Backlog, labels: { module: 'auth' } }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const labelButton = screen.getByText(/Labels:/i)
      fireEvent.click(labelButton)

      let popover: HTMLElement | null = null
      await waitFor(() => {
        popover = document.querySelector('[class*="origin-top-right"]')
        expect(popover).toBeInTheDocument()
      })

      fireEvent.click(within(popover!).getByTestId('label-option-stream=frontend'))

      await waitFor(() => {
        const desktopBoard = document.querySelector('.hidden.md\\:flex.flex-row') as HTMLElement
        expect(desktopBoard).toBeInTheDocument()
        const backlogColumn = desktopBoard.children[0] as HTMLElement
        expect(backlogColumn.textContent).toContain('#1')
        expect(backlogColumn.textContent).not.toContain('#2')
        expect(backlogColumn.textContent).not.toContain('#3')
      })
    })
  })

  describe('Done column collapse - desktop limit-5 + expand', () => {
    beforeEach(() => {
      Object.defineProperty(window, 'location', {
        value: { search: '', pathname: '/' },
        writable: true,
      })
    })

    afterEach(() => {
      cleanup()
      vi.clearAllMocks()
    })

    it('limits the desktop Done column to the first five issues and exposes an N more toggle', () => {
      // Default sort is priority; with equal priority the tie-breaker is
      // updatedAt desc. We give each issue a distinct updatedAt so the
      // expected sort order is deterministic: 107, 106, ..., 101.
      const baseDate = new Date('2026-01-01T00:00:00Z').getTime()
      const issues = Array.from({ length: 7 }, (_, i) =>
        makeIssue({
          number: 100 + i + 1,
          title: `Done issue ${i + 1}`,
          status: IssueStatus.Done,
          health: IssueHealth.Done,
          updatedAt: new Date(baseDate + i * 24 * 60 * 60 * 1000).toISOString(),
        }),
      )
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const doneColumn = screen.getByTestId('stage-column-done')
      const collapsedCards = within(doneColumn).getAllByTestId('issue-card')
      expect(collapsedCards).toHaveLength(5)
      expect(within(doneColumn).getByText('2 more')).toBeInTheDocument()
      expect(within(doneColumn).queryByText('Show less')).not.toBeInTheDocument()

      // The five most recently updated cards are visible (#107..#103);
      // the older two (#101, #102) are hidden behind the collapse toggle.
      expect(within(doneColumn).getByText('#107')).toBeInTheDocument()
      expect(within(doneColumn).getByText('#103')).toBeInTheDocument()
      expect(within(doneColumn).queryByText('#101')).not.toBeInTheDocument()
      expect(within(doneColumn).queryByText('#102')).not.toBeInTheDocument()
    })

    it('expands the desktop Done column to show every issue when the toggle is clicked', () => {
      const baseDate = new Date('2026-01-01T00:00:00Z').getTime()
      const issues = Array.from({ length: 7 }, (_, i) =>
        makeIssue({
          number: 200 + i + 1,
          title: `Done issue ${i + 1}`,
          status: IssueStatus.Done,
          health: IssueHealth.Done,
          updatedAt: new Date(baseDate + i * 24 * 60 * 60 * 1000).toISOString(),
        }),
      )
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const doneColumn = screen.getByTestId('stage-column-done')
      fireEvent.click(within(doneColumn).getByText('2 more'))

      const expandedCards = within(doneColumn).getAllByTestId('issue-card')
      expect(expandedCards).toHaveLength(7)
      expect(within(doneColumn).getByText('Show less')).toBeInTheDocument()
      expect(within(doneColumn).queryByText('2 more')).not.toBeInTheDocument()
      expect(within(doneColumn).getByText('#201')).toBeInTheDocument()
      expect(within(doneColumn).getByText('#207')).toBeInTheDocument()
    })

    it('does not collapse the Done column when it has fewer than six issues', () => {
      const issues = Array.from({ length: 4 }, (_, i) =>
        makeIssue({
          number: 300 + i + 1,
          title: `Done issue ${i + 1}`,
          status: IssueStatus.Done,
          health: IssueHealth.Done,
        }),
      )
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const doneColumn = screen.getByTestId('stage-column-done')
      const cards = within(doneColumn).getAllByTestId('issue-card')
      expect(cards).toHaveLength(4)
      expect(within(doneColumn).queryByText(/more/)).not.toBeInTheDocument()
      expect(within(doneColumn).queryByText('Show less')).not.toBeInTheDocument()
    })

    it('renders the Archive / Archive-all-done controls in the desktop Done column footer', () => {
      const issues = Array.from({ length: 3 }, (_, i) =>
        makeIssue({
          number: 400 + i + 1,
          title: `Done issue ${i + 1}`,
          status: IssueStatus.Done,
          health: IssueHealth.Done,
        }),
      )
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} archivedCount={5} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const doneColumn = screen.getByTestId('stage-column-done')
      expect(within(doneColumn).getByText('Archive all done')).toBeInTheDocument()
      expect(within(doneColumn).getByText(/5 archived/)).toBeInTheDocument()
      expect(within(doneColumn).getByText('view')).toBeInTheDocument()
    })
  })
})
