// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { IssueStatus, type Issue, type IssueHealth } from '../../../entities/issue'

const useIssuesMock = vi.fn()
vi.mock('../../../entities/issue/api/queries', () => ({
  useIssues: (...args: unknown[]) => useIssuesMock(...args),
}))

import { SnapshotRow } from './SnapshotRow'

type StatusLiteral = 'backlog' | 'in_progress' | 'done' | 'cancelled'

function makeIssue(overrides: {
  status: StatusLiteral
  createdAt: string
  updatedAt: string
  id?: string
  number?: number
}): Issue {
  return {
    id: overrides.id ?? `id-${Math.random().toString(36).slice(2)}`,
    number: overrides.number ?? 1,
    title: 'title',
    status: overrides.status as IssueStatus,
    health: 'active' as IssueHealth,
    projectId: 'proj-1',
    labels: {},
    createdAt: overrides.createdAt,
    updatedAt: overrides.updatedAt,
    isDraft: false,
    canStart: true,
    blocker: null,
  }
}

const NOW = new Date('2026-06-19T12:00:00.000Z').getTime()
const ONE_DAY_MS = 24 * 60 * 60 * 1000

function daysAgo(days: number): string {
  return new Date(NOW - days * ONE_DAY_MS).toISOString()
}

function renderRow() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider>
        <MemoryRouter initialEntries={['/']}>
          <SnapshotRow />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('SnapshotRow', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.useFakeTimers()
    vi.setSystemTime(new Date(NOW))
  })

  afterEach(() => {
    vi.useRealTimers()
    cleanup()
  })

  it('renders the three counts labeled as Completed, Failed, and New', () => {
    useIssuesMock.mockReturnValue({
      data: [
        ...Array.from({ length: 5 }, () =>
          makeIssue({ status: 'done', createdAt: daysAgo(20), updatedAt: daysAgo(1) }),
        ),
        makeIssue({ status: 'cancelled', createdAt: daysAgo(30), updatedAt: daysAgo(2) }),
        ...Array.from({ length: 8 }, () =>
          makeIssue({ status: 'backlog', createdAt: daysAgo(1), updatedAt: daysAgo(10) }),
        ),
      ],
    })

    renderRow()

    const completed = screen.getByTestId('productivity-snapshot-completed')
    const failed = screen.getByTestId('productivity-snapshot-failed')
    const newly = screen.getByTestId('productivity-snapshot-new')

    expect(completed).toHaveTextContent('Completed')
    expect(completed).toHaveTextContent('5')

    expect(failed).toHaveTextContent('Failed')
    expect(failed).toHaveTextContent('1')

    expect(newly).toHaveTextContent('New')
    expect(newly).toHaveTextContent('8')
  })

  it('renders all-zero counts visibly when the snapshot returns zeros for the week', () => {
    useIssuesMock.mockReturnValue({
      data: [
        makeIssue({ status: 'backlog', createdAt: daysAgo(30), updatedAt: daysAgo(20) }),
        makeIssue({ status: 'in_progress', createdAt: daysAgo(30), updatedAt: daysAgo(20) }),
      ],
    })

    const { container } = renderRow()

    const row = screen.getByTestId('productivity-snapshot-row')
    expect(row).toBeInTheDocument()
    expect(row).not.toHaveAttribute('data-state', 'empty')

    const completed = screen.getByTestId('productivity-snapshot-completed')
    const failed = screen.getByTestId('productivity-snapshot-failed')
    const newly = screen.getByTestId('productivity-snapshot-new')

    expect(completed).toHaveTextContent('0')
    expect(failed).toHaveTextContent('0')
    expect(newly).toHaveTextContent('0')

    expect(screen.queryByTestId('productivity-snapshot-empty')).not.toBeInTheDocument()
    expect(container.querySelector('[data-state="empty"]')).toBeNull()
  })

  it('renders a meaningful empty state when the project has no issues', () => {
    useIssuesMock.mockReturnValue({ data: [] })

    renderRow()

    const row = screen.getByTestId('productivity-snapshot-row')
    expect(row).toBeInTheDocument()
    expect(row).toHaveAttribute('data-state', 'empty')

    const empty = screen.getByTestId('productivity-snapshot-empty')
    expect(empty).toBeInTheDocument()
    expect(empty.textContent ?? '').toMatch(/no issues/i)

    expect(screen.queryByTestId('productivity-snapshot-completed')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-snapshot-failed')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-snapshot-new')).not.toBeInTheDocument()
  })

  it('renders a meaningful empty state when useIssues data is undefined', () => {
    useIssuesMock.mockReturnValue({ data: undefined })

    renderRow()

    const row = screen.getByTestId('productivity-snapshot-row')
    expect(row).toHaveAttribute('data-state', 'empty')
    expect(screen.getByTestId('productivity-snapshot-empty')).toBeInTheDocument()
  })
})
