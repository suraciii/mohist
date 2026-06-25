// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { EpicStatus, type EpicPriority, type EpicProgress, type EpicWithProgress } from '../../../entities/epic'

const useEpicsMock = vi.fn()
vi.mock('../../../entities/epic/api/queries', () => ({
  useEpics: (...args: unknown[]) => useEpicsMock(...args),
}))

import { EpicProgressList } from './EpicProgressList'

function makeEpic(overrides: {
  id: string
  priority: EpicPriority
  status?: EpicStatus
  deliveredCount: number
  totalIssueCount: number
  title?: string
  number?: number
}): EpicWithProgress {
  const progress: EpicProgress = {
    deliveredCount: overrides.deliveredCount,
    totalIssueCount: overrides.totalIssueCount,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: null,
    nextIssueReason: null,
    readyToMarkDone: false,
  }
  return {
    id: overrides.id,
    number: overrides.number ?? 1,
    title: overrides.title ?? `Epic ${overrides.id}`,
    description: '',
    priority: overrides.priority,
    status: overrides.status ?? EpicStatus.Idle,
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    progress,
  }
}

function renderList() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider>
        <MemoryRouter initialEntries={['/']}>
          <EpicProgressList />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('EpicProgressList', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    cleanup()
  })

  it('renders a progress bar for each of three in-progress Epics with proportional fill', () => {
    useEpicsMock.mockReturnValue({
      data: [
        makeEpic({ id: 'a', priority: 'p1', deliveredCount: 2, totalIssueCount: 4, number: 1 }),
        makeEpic({ id: 'b', priority: 'p0', deliveredCount: 1, totalIssueCount: 2, number: 2 }),
        makeEpic({ id: 'c', priority: 'p2', deliveredCount: 3, totalIssueCount: 6, number: 3 }),
      ],
    })

    const { container } = renderList()

    const section = screen.getByTestId('productivity-epic-list')
    expect(section).toBeInTheDocument()
    expect(section).not.toHaveAttribute('data-state', 'empty')

    expect(screen.getByTestId('productivity-epic-list-item-0')).toBeInTheDocument()
    expect(screen.getByTestId('productivity-epic-list-item-1')).toBeInTheDocument()
    expect(screen.getByTestId('productivity-epic-list-item-2')).toBeInTheDocument()

    const bars = container.querySelectorAll('[data-testid^="productivity-epic-list-bar-"]')
    expect(bars).toHaveLength(3)

    const fills = Array.from(bars).map(bar =>
      bar.querySelector('div') as HTMLElement,
    )
    expect(fills[0].style.width).toBe('50%')
    expect(fills[1].style.width).toBe('50%')
    expect(fills[2].style.width).toBe('50%')
  })

  it('sorts by priority then by delivered/total ratio', () => {
    useEpicsMock.mockReturnValue({
      data: [
        makeEpic({ id: 'p2-low', priority: 'p2', deliveredCount: 0, totalIssueCount: 4, number: 11 }),
        makeEpic({ id: 'p0-high', priority: 'p0', deliveredCount: 3, totalIssueCount: 4, number: 22 }),
        makeEpic({ id: 'p1', priority: 'p1', deliveredCount: 1, totalIssueCount: 4, number: 33 }),
      ],
    })

    const { container } = renderList()

    const item0 = screen.getByTestId('productivity-epic-list-item-0')
    const item1 = screen.getByTestId('productivity-epic-list-item-1')
    const item2 = screen.getByTestId('productivity-epic-list-item-2')

    expect(item0.textContent).toMatch(/#22/)
    expect(item1.textContent).toMatch(/#33/)
    expect(item2.textContent).toMatch(/#11/)

    const bars = container.querySelectorAll('[data-testid^="productivity-epic-list-bar-"]')
    const fills = Array.from(bars).map(bar =>
      bar.querySelector('div') as HTMLElement,
    )
    expect(fills[0].style.width).toBe('75%')
    expect(fills[1].style.width).toBe('25%')
    expect(fills[2].style.width).toBe('0%')
  })

  it('caps the visible list at three and surfaces a +N more affordance', () => {
    useEpicsMock.mockReturnValue({
      data: [
        makeEpic({ id: 'a', priority: 'p0', deliveredCount: 1, totalIssueCount: 2, number: 1 }),
        makeEpic({ id: 'b', priority: 'p1', deliveredCount: 1, totalIssueCount: 2, number: 2 }),
        makeEpic({ id: 'c', priority: 'p2', deliveredCount: 1, totalIssueCount: 2, number: 3 }),
        makeEpic({ id: 'd', priority: 'p3', deliveredCount: 1, totalIssueCount: 2, number: 4 }),
        makeEpic({ id: 'e', priority: 'p4', deliveredCount: 1, totalIssueCount: 2, number: 5 }),
      ],
    })

    renderList()

    expect(screen.getByTestId('productivity-epic-list-item-0')).toBeInTheDocument()
    expect(screen.getByTestId('productivity-epic-list-item-1')).toBeInTheDocument()
    expect(screen.getByTestId('productivity-epic-list-item-2')).toBeInTheDocument()
    expect(screen.queryByTestId('productivity-epic-list-item-3')).not.toBeInTheDocument()

    expect(screen.getByTestId('productivity-epic-list-more')).toHaveTextContent('+2 more')
  })

  it('does not show the +N more affordance when exactly three in-progress Epics exist', () => {
    useEpicsMock.mockReturnValue({
      data: [
        makeEpic({ id: 'a', priority: 'p0', deliveredCount: 1, totalIssueCount: 2, number: 1 }),
        makeEpic({ id: 'b', priority: 'p1', deliveredCount: 1, totalIssueCount: 2, number: 2 }),
        makeEpic({ id: 'c', priority: 'p2', deliveredCount: 1, totalIssueCount: 2, number: 3 }),
      ],
    })

    renderList()

    expect(screen.queryByTestId('productivity-epic-list-more')).not.toBeInTheDocument()
  })

  it('renders an empty state when fewer than two in-progress Epics exist', () => {
    useEpicsMock.mockReturnValue({
      data: [
        makeEpic({ id: 'a', priority: 'p0', deliveredCount: 1, totalIssueCount: 2, number: 1 }),
      ],
    })

    renderList()

    const section = screen.getByTestId('productivity-epic-list')
    expect(section).toHaveAttribute('data-state', 'empty')

    const empty = screen.getByTestId('productivity-epic-list-empty')
    expect(empty).toBeInTheDocument()
    expect(empty.textContent ?? '').toMatch(/no active epics/i)

    expect(screen.queryByTestId('productivity-epic-list-item-0')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-epic-list-more')).not.toBeInTheDocument()
  })

  it('renders an empty state when zero in-progress Epics exist (mixed statuses)', () => {
    useEpicsMock.mockReturnValue({
      data: [
        makeEpic({ id: 'd', priority: 'p0', deliveredCount: 2, totalIssueCount: 2, number: 1, status: EpicStatus.Done }),
        makeEpic({ id: 'c', priority: 'p0', deliveredCount: 1, totalIssueCount: 2, number: 2, status: EpicStatus.Closed }),
      ],
    })

    renderList()

    const section = screen.getByTestId('productivity-epic-list')
    expect(section).toHaveAttribute('data-state', 'empty')
    expect(screen.getByTestId('productivity-epic-list-empty')).toBeInTheDocument()
  })

  it('renders an empty state when useEpics data is undefined', () => {
    useEpicsMock.mockReturnValue({ data: undefined })

    renderList()

    const section = screen.getByTestId('productivity-epic-list')
    expect(section).toHaveAttribute('data-state', 'empty')
    expect(screen.getByTestId('productivity-epic-list-empty')).toBeInTheDocument()
  })

  it('guards totalIssueCount === 0 by rendering an empty 0% bar without NaN', () => {
    useEpicsMock.mockReturnValue({
      data: [
        makeEpic({ id: 'zero-a', priority: 'p0', deliveredCount: 0, totalIssueCount: 0, number: 1 }),
        makeEpic({ id: 'zero-b', priority: 'p1', deliveredCount: 0, totalIssueCount: 0, number: 2 }),
      ],
    })

    const { container } = renderList()

    expect(screen.getByTestId('productivity-epic-list-item-0')).toBeInTheDocument()
    expect(screen.getByTestId('productivity-epic-list-item-1')).toBeInTheDocument()

    const section = screen.getByTestId('productivity-epic-list')
    expect(section).not.toHaveAttribute('data-state', 'empty')

    const bars = container.querySelectorAll('[data-testid^="productivity-epic-list-bar-"]')
    expect(bars).toHaveLength(2)

    const fills = Array.from(bars).map(bar =>
      bar.querySelector('div') as HTMLElement,
    )
    expect(fills[0].style.width).toBe('0%')
    expect(fills[1].style.width).toBe('0%')
    expect(fills[0].style.width).not.toMatch(/NaN/)
    expect(fills[1].style.width).not.toMatch(/NaN/)
  })
})
