import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { IssueStatus, IssueHealth, WorkflowStage } from '../../../entities/issue'
import { makeIssue, makeIssues, mockAgentStatus } from './_kanbanBoardQueryTestUtils'
import { KanbanBoard } from './KanbanBoard'
function renderBoard(issues = makeIssues(3, { status: IssueStatus.Backlog })) {
  const queryClient = new QueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}
function getMobileBoardContainer(): HTMLElement {
  const container = document.querySelector<HTMLElement>('.md\\:hidden.flex.flex-col')
  if (!container) {
    throw new Error('Mobile board container (md:hidden flex flex-col) not found')
  }
  return container
}
function getMobileStageTabsStrip(): HTMLElement {
  const mobile = getMobileBoardContainer()
  const firstTab = within(mobile).getByTestId('mobile-stage-tab-backlog')
  const strip = firstTab.parentElement
  if (!strip) {
    throw new Error('Mobile stage-tab strip parent not found')
  }
  return strip as HTMLElement
}
function getMobileCardList(): HTMLElement {
  const mobile = getMobileBoardContainer()
  const cards = within(mobile).getAllByTestId('issue-card')
  if (cards.length === 0) {
    throw new Error('No issue-card elements found inside the mobile list')
  }
  const list = cards[0].parentElement
  if (!list) {
    throw new Error('Mobile card list parent not found')
  }
  return list as HTMLElement
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

describe('Mobile board navigation non-overlap', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  describe('Stage tab strip is a snap scroll strip of all four status groups', () => {
    it('renders all four mobile-stage-tab elements (backlog, in_progress, done, cancelled) inside the mobile board container', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog }),
        makeIssue({ number: 2, status: IssueStatus.InProgress }),
        makeIssue({ number: 3, status: IssueStatus.Done }),
        makeIssue({ number: 4, status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
      ]
      renderBoard(issues)

      const container = getMobileBoardContainer()

      expect(within(container).getByTestId('mobile-stage-tab-backlog')).toBeInTheDocument()
      expect(within(container).getByTestId('mobile-stage-tab-in_progress')).toBeInTheDocument()
      expect(within(container).getByTestId('mobile-stage-tab-done')).toBeInTheDocument()
      expect(within(container).getByTestId('mobile-stage-tab-cancelled')).toBeInTheDocument()
    })

    it('places the four stage tabs as direct children of the snap scroll strip so they snap horizontally', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog }),
        makeIssue({ number: 2, status: IssueStatus.InProgress }),
        makeIssue({ number: 3, status: IssueStatus.Done }),
        makeIssue({ number: 4, status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
      ]
      renderBoard(issues)

      const strip = getMobileStageTabsStrip()
      const stripClasses = strip.className.split(/\s+/)

      expect(stripClasses).toContain('overflow-x-auto')
      expect(stripClasses).toContain('snap-x')
      expect(stripClasses).toContain('flex')

      const tabsInStrip = strip.querySelectorAll('[data-testid^="mobile-stage-tab-"]')
      expect(tabsInStrip.length).toBe(4)

      const mobile = getMobileBoardContainer()
      const expectedTabIds = [
        'mobile-stage-tab-backlog',
        'mobile-stage-tab-in_progress',
        'mobile-stage-tab-done',
        'mobile-stage-tab-cancelled',
      ]
      expectedTabIds.forEach((id) => {
        const tab = within(mobile).getByTestId(id)
        expect(strip.contains(tab)).toBe(true)
        expect(tab.parentElement).toBe(strip)
      })
    })

    it('exposes the snap scroll strip as a direct child of the mobile board container (md:hidden flex flex-col)', () => {
      const issues = [makeIssue({ number: 1, status: IssueStatus.Backlog })]
      renderBoard(issues)

      const container = getMobileBoardContainer()
      const strip = getMobileStageTabsStrip()

      expect(strip.parentElement).toBe(container)
    })
  })

  describe('Stage tab strip and card list are distinct flex-col siblings that stack vertically', () => {
    it('marks the stage tab strip with border-b and shrink-0 so it stays a thin header strip', () => {
      const issues = [makeIssue({ number: 1, status: IssueStatus.Backlog })]
      renderBoard(issues)

      const strip = getMobileStageTabsStrip()
      const stripClasses = strip.className.split(/\s+/)

      expect(stripClasses).toContain('border-b')
      expect(stripClasses).toContain('shrink-0')
    })

    it('marks the card list with flex-1 and overflow-y-auto so it fills remaining vertical space without horizontal overlap', () => {
      const issues = [makeIssue({ number: 1, status: IssueStatus.Backlog })]
      renderBoard(issues)

      const cardList = getMobileCardList()
      const listClasses = cardList.className.split(/\s+/)

      expect(listClasses).toContain('flex-1')
      expect(listClasses).toContain('overflow-y-auto')
    })

    it('renders the strip and the card list as distinct siblings inside the mobile board container, neither nested inside the other', () => {
      const issues = [
        makeIssue({ number: 1, status: IssueStatus.Backlog }),
        makeIssue({ number: 2, status: IssueStatus.InProgress }),
      ]
      renderBoard(issues)

      const container = getMobileBoardContainer()
      const strip = getMobileStageTabsStrip()
      const cardList = getMobileCardList()

      expect(container.contains(strip)).toBe(true)
      expect(container.contains(cardList)).toBe(true)
      expect(strip.contains(cardList)).toBe(false)
      expect(cardList.contains(strip)).toBe(false)

      expect(strip.parentElement).toBe(container)
      expect(cardList.parentElement).toBe(container)
      expect(strip.parentElement).not.toBe(cardList)
    })

    it('places the stage tab strip before the card list in DOM order so they stack vertically rather than overlap horizontally', () => {
      const issues = [makeIssue({ number: 1, status: IssueStatus.Backlog })]
      renderBoard(issues)

      const strip = getMobileStageTabsStrip()
      const cardList = getMobileCardList()

      const position =
        strip.compareDocumentPosition(cardList) & Node.DOCUMENT_POSITION_FOLLOWING
      expect(position).toBeTruthy()
    })

    it('does not place the strip and the card list inside a shared flex-row parent that would force horizontal overlap', () => {
      const issues = [makeIssue({ number: 1, status: IssueStatus.Backlog })]
      renderBoard(issues)

      const container = getMobileBoardContainer()
      const strip = getMobileStageTabsStrip()
      const cardList = getMobileCardList()

      expect(container.className.split(/\s+/)).toContain('flex-col')

      const containerClasses = container.className.split(/\s+/)
      const hasFlexRow = containerClasses.some(
        (cls) => cls === 'flex-row' || cls.startsWith('flex-row'),
      )
      expect(hasFlexRow).toBe(false)

      const sharedAncestor = findCommonAncestor(strip, cardList)
      expect(sharedAncestor).toBe(container)
    })
  })

  describe('Cards navigate to issue detail and are unobstructed by other surfaces', () => {
    it('renders a mobile issue-card as a link (anchor) with an href that contains the issue number', () => {
      const issues = [
        makeIssue({ number: 211, status: IssueStatus.Backlog, title: 'Tap-target navigation' }),
        makeIssue({ number: 212, status: IssueStatus.Backlog, title: 'Second card' }),
      ]
      renderBoard(issues)

      const mobile = getMobileBoardContainer()
      const cards = within(mobile).getAllByTestId('issue-card')
      expect(cards.length).toBe(2)

      cards.forEach((card) => {
        expect(card.tagName).toBe('A')
      })

      const hrefs = cards.map((c) => c.getAttribute('href') ?? '')
      expect(hrefs.some((h) => h.includes('211'))).toBe(true)
      expect(hrefs.some((h) => h.includes('212'))).toBe(true)
    })

    it('does not nest the issue-card link inside the filter bar so the tap target is unobstructed', () => {
      const issues = [makeIssue({ number: 311, status: IssueStatus.Backlog })]
      renderBoard(issues)

      const mobile = getMobileBoardContainer()
      const card = within(mobile).getByTestId('issue-card')

      const filterBarRoot = document.querySelector<HTMLElement>(
        '[class*="bg-background"][class*="border-b"]',
      )
      expect(filterBarRoot).not.toBeNull()
      expect(filterBarRoot!.contains(card)).toBe(false)
    })

    it('does not nest the issue-card link inside the mobile stage tab strip so the tap target is unobstructed', () => {
      const issues = [makeIssue({ number: 411, status: IssueStatus.Backlog })]
      renderBoard(issues)

      const mobile = getMobileBoardContainer()
      const card = within(mobile).getByTestId('issue-card')
      const strip = getMobileStageTabsStrip()

      expect(strip.contains(card)).toBe(false)

      let cur: Element | null = card.parentElement
      while (cur) {
        expect(cur).not.toBe(strip)
        cur = cur.parentElement
      }
    })

    it('does not obstruct the issue-card tap target with a pointer-events-none surface', () => {
      const issues = [
        makeIssue({ number: 511, status: IssueStatus.Backlog, title: 'Active card' }),
      ]
      renderBoard(issues)

      const mobile = getMobileBoardContainer()
      const card = within(mobile).getByTestId('issue-card')
      const cardStyles = window.getComputedStyle(card)
      expect(cardStyles.pointerEvents).not.toBe('none')
    })
  })

  describe('Primary board action (rerun / resume) is reachable on a mobile card', () => {
    it('renders the rerun-button inside the mobile card list for an interrupted card', () => {
      const issues = [
        makeIssue({
          number: 611,
          status: IssueStatus.InProgress,
          health: IssueHealth.Active,
          workflowStage: WorkflowStage.Build,
          title: 'Interrupted work',
        }),
      ]
      renderBoard(issues)

      const cardList = getMobileCardList()
      expect(within(cardList).getByTestId('rerun-button')).toBeInTheDocument()
    })

    it('renders the rerun-button inside the mobile card list for a rerunnable active backlog card', () => {
      const issues = [
        makeIssue({
          number: 711,
          status: IssueStatus.Backlog,
          health: IssueHealth.Active,
          workflowStage: WorkflowStage.Plan,
          title: 'Rerunnable work',
        }),
      ]
      renderBoard(issues)

      const cardList = getMobileCardList()
      expect(within(cardList).getByTestId('rerun-button')).toBeInTheDocument()
    })

    it('keeps the rerun-button nested inside its own card surface (not overlapping a sibling card)', () => {
      const issues = [
        makeIssue({
          number: 811,
          status: IssueStatus.InProgress,
          health: IssueHealth.Active,
          workflowStage: WorkflowStage.Build,
          title: 'Interrupted one',
        }),
        makeIssue({
          number: 812,
          status: IssueStatus.InProgress,
          health: IssueHealth.Active,
          workflowStage: WorkflowStage.Build,
          title: 'Interrupted two',
        }),
      ]
      renderBoard(issues)

      const mobile = getMobileBoardContainer()
      const buttons = within(mobile).getAllByTestId('rerun-button')
      expect(buttons.length).toBe(2)

      const cards = within(mobile).getAllByTestId('issue-card')
      expect(cards.length).toBe(2)

      buttons.forEach((button, index) => {
        const owningCard = cards[index]
        expect(owningCard.contains(button)).toBe(true)
      })

      const cardList = getMobileCardList()
      expect(cardList.contains(buttons[0])).toBe(true)
      expect(cardList.contains(buttons[1])).toBe(true)
    })

    it('does not nest the rerun-button inside the stage tab strip so the action is reachable on the card', () => {
      const issues = [
        makeIssue({
          number: 911,
          status: IssueStatus.InProgress,
          health: IssueHealth.Active,
          workflowStage: WorkflowStage.Build,
          title: 'Interrupted work',
        }),
      ]
      renderBoard(issues)

      const mobile = getMobileBoardContainer()
      const button = within(mobile).getByTestId('rerun-button')
      const strip = getMobileStageTabsStrip()

      expect(strip.contains(button)).toBe(false)
    })

    it('marks the rerun-button as clickable (not disabled and not obstructed) inside the mobile card list', () => {
      const issues = [
        makeIssue({
          number: 1011,
          status: IssueStatus.InProgress,
          health: IssueHealth.Active,
          workflowStage: WorkflowStage.Build,
          title: 'Clickable rerun',
        }),
      ]
      renderBoard(issues)

      const mobile = getMobileBoardContainer()
      const button = within(mobile).getByTestId('rerun-button')
      expect(button).not.toBeDisabled()

      const cardList = getMobileCardList()
      expect(cardList.contains(button)).toBe(true)
    })
  })
})
