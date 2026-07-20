import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { IssueStatus } from '../../../entities/issue'
import { makeIssue, makeIssues, mockAgentStatus } from './_kanbanBoardQueryTestUtils'

import { KanbanBoard } from './KanbanBoard'

function renderWith(
  issues = makeIssues(3, { status: IssueStatus.Backlog }),
) {
  const queryClient = new QueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function getDesktopFilterBar(): HTMLElement {
  const filterBar = document.querySelector<HTMLElement>(
    '.hidden.md\\:flex.flex-wrap',
  )
  if (!filterBar) {
    throw new Error('Desktop FilterBar (hidden md:flex flex-wrap) not found')
  }
  return filterBar
}

function getFilterBarRoot(): HTMLElement {
  return getDesktopFilterBar().closest<HTMLElement>('[class*="bg-background"][class*="border-b"]')!
}

function getMobileFilterControls(): HTMLElement {
  const mobileControls = document.querySelector<HTMLElement>(
    '.md\\:hidden.px-3.py-2',
  )
  if (!mobileControls) {
    throw new Error('Mobile filter controls section (md:hidden px-3 py-2) not found')
  }
  return mobileControls
}

function getMobileStageTabsStrip(): HTMLElement {
  const firstTab = screen.queryByTestId('mobile-stage-tab-backlog')
  if (!firstTab) {
    throw new Error('Mobile stage-tab-backlog not found')
  }
  const strip = firstTab.parentElement
  if (!strip) {
    throw new Error('Mobile stage-tab strip parent not found')
  }
  return strip as HTMLElement
}

function findCommonAncestor(a: Element, b: Element): Element {
  const ancestors = new Set<Element>()
  let cur: Element | null = a
  while (cur) {
    ancestors.add(cur)
    cur = cur.parentElement
  }
  cur = b
  while (cur && !ancestors.has(cur)) {
    cur = cur.parentElement
  }
  return cur as Element
}

describe('First-screen filter/search/sort reachability', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  describe('Desktop FilterBar is a single compact row above the board', () => {
    it('exposes search-input, priority chips, label affordance, repository selector, and all three sort buttons in a single flex-wrap row', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog, labels: { kind: 'bug' }, repositoryName: 'web' }),
        makeIssue({
          number: 2,
          status: IssueStatus.InProgress,
          labels: { kind: 'feature' },
          repositoryName: 'server',
        }),
        makeIssue({ number: 3, status: IssueStatus.Done, repositoryName: 'web' }),
      ]
      renderWith(issues)

      const filterBar = getDesktopFilterBar()

      expect(within(filterBar).getByTestId('search-input')).toBeInTheDocument()
      expect(within(filterBar).getByTestId('priority-chip-p0')).toBeInTheDocument()
      expect(within(filterBar).getByTestId('priority-chip-p1')).toBeInTheDocument()
      expect(within(filterBar).getByTestId('priority-chip-p2')).toBeInTheDocument()
      expect(within(filterBar).getByTestId('label-chip')).toBeInTheDocument()
      expect(within(filterBar).getByTestId('repository-filter')).toBeInTheDocument()
      expect(within(filterBar).getByTestId('sort-priority')).toBeInTheDocument()
      expect(within(filterBar).getByTestId('sort-number')).toBeInTheDocument()
      expect(within(filterBar).getByTestId('sort-updated')).toBeInTheDocument()
    })

    it('renders the FilterBar as a single hidden md:flex flex-wrap row', () => {
      const issues = [makeIssue({ number: 1, status: IssueStatus.Backlog })]
      renderWith(issues)

      const filterBar = getDesktopFilterBar()
      const classes = filterBar.className.split(/\s+/)
      expect(classes).toContain('hidden')
      expect(classes).toContain('md:flex')
      expect(classes).toContain('flex-wrap')
    })

    it('places kanban-board-row after the FilterBar in DOM order within kanban-board-root', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog }),
        makeIssue({ number: 2, status: IssueStatus.InProgress }),
        makeIssue({ number: 3, status: IssueStatus.Done }),
      ]
      renderWith(issues)

      const root = screen.getByTestId('kanban-board-root')
      const filterBar = getDesktopFilterBar()
      const boardRow = screen.getByTestId('kanban-board-row')

      expect(root.contains(filterBar)).toBe(true)
      expect(root.contains(boardRow)).toBe(true)

      const position =
        filterBar.compareDocumentPosition(boardRow) & Node.DOCUMENT_POSITION_FOLLOWING
      expect(position).toBeTruthy()
    })
  })

  describe('Mobile controls stay reachable without overlap', () => {
    it('renders the search input and mobile-filter-toggle inside the mobile filter controls section', () => {
      const issues = [makeIssue({ number: 1, status: IssueStatus.Backlog })]
      renderWith(issues)

      const mobileControls = getMobileFilterControls()

      expect(within(mobileControls).getByTestId('search-input')).toBeInTheDocument()
      expect(
        within(mobileControls).getByTestId('mobile-filter-toggle'),
      ).toBeInTheDocument()
    })

    it('marks the mobile filter controls section as md:hidden so it only renders on narrow viewports', () => {
      const issues = [makeIssue({ number: 1, status: IssueStatus.Backlog })]
      renderWith(issues)

      const mobileControls = getMobileFilterControls()
      const classes = mobileControls.className.split(/\s+/)
      expect(classes).toContain('md:hidden')
      expect(classes).not.toContain('md:flex')
    })

    it('reveals the mobile-filter-panel as a block panel (not inline beside the card list) when the toggle is clicked', () => {
      const issues = [makeIssue({ number: 1, status: IssueStatus.Backlog })]
      renderWith(issues)

      expect(screen.queryByTestId('mobile-filter-panel')).not.toBeInTheDocument()

      fireEvent.click(screen.getByTestId('mobile-filter-toggle'))

      const panel = screen.getByTestId('mobile-filter-panel')
      expect(panel).toBeInTheDocument()

      const classes = panel.className.split(/\s+/)
      expect(classes).toContain('space-y-2')
      expect(classes).not.toContain('inline-flex')
      expect(classes).not.toContain('inline-block')

      const stageTabs = getMobileStageTabsStrip()
      const position =
        panel.compareDocumentPosition(stageTabs) & Node.DOCUMENT_POSITION_FOLLOWING
      expect(position).toBeTruthy()
    })

    it('keeps the FilterBar and the mobile stage-tab container as distinct sibling containers within kanban-board-root, neither nested inside the other', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog }),
        makeIssue({ number: 2, status: IssueStatus.InProgress }),
        makeIssue({ number: 3, status: IssueStatus.Done }),
      ]
      renderWith(issues)

      const root = screen.getByTestId('kanban-board-root')
      const filterBarRoot = getFilterBarRoot()
      const stageTabs = getMobileStageTabsStrip()

      expect(root.contains(filterBarRoot)).toBe(true)
      expect(root.contains(stageTabs)).toBe(true)
      expect(filterBarRoot.contains(stageTabs)).toBe(false)
      expect(stageTabs.contains(filterBarRoot)).toBe(false)

      expect(filterBarRoot.parentElement).toBe(root)
      expect(stageTabs.parentElement).not.toBe(filterBarRoot)
    })

    it('does not stack the FilterBar and the stage-tab strip side by side via a shared flex-row parent', () => {
      const issues = [makeIssue({ number: 1, status: IssueStatus.Backlog })]
      renderWith(issues)

      const root = screen.getByTestId('kanban-board-root')
      const filterBarRoot = getFilterBarRoot()
      const stageTabs = getMobileStageTabsStrip()

      const tabsClasses = stageTabs.className.split(/\s+/)

      expect(tabsClasses).toContain('overflow-x-auto')

      const tabsHasFlexRow = tabsClasses.some(
        (cls) => cls === 'flex-row' || cls.startsWith('flex-row'),
      )
      expect(tabsHasFlexRow).toBe(false)

      const filterBarClasses = filterBarRoot.className.split(/\s+/)
      const filterHasFlexRow = filterBarClasses.some(
        (cls) => cls === 'flex-row' || cls.startsWith('flex-row'),
      )
      expect(filterHasFlexRow).toBe(false)

      const sharedAncestor = findCommonAncestor(filterBarRoot, stageTabs)
      expect(sharedAncestor).toBe(root)

      expect(filterBarRoot.parentElement).toBe(root)
      expect(stageTabs.parentElement).not.toBe(filterBarRoot)
    })
  })
})
