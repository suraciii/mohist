// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import type { ReactNode } from 'react'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueStatus, IssueHealth, type Issue } from '../../../entities/issue'
import { DashboardDigestWidget } from './DashboardDigestWidget'
import { DigestRow } from './DigestRow'

const { useIssuesMock, useArchivedIssuesMock } = vi.hoisted(() => ({
  useIssuesMock: vi.fn(),
  useArchivedIssuesMock: vi.fn(),
}))

vi.mock('../../../entities/issue/api/queries', () => ({
  useIssues: (...args: unknown[]) => useIssuesMock(...args),
  useArchivedIssues: (...args: unknown[]) => useArchivedIssuesMock(...args),
}))

const projects: Project[] = [
  {
    id: 'proj-1',
    name: 'Project 1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    repositories: [],
  },
]

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: overrides.id ?? `id-${Math.random().toString(36).slice(2)}`,
    number: overrides.number ?? 1,
    title: overrides.title ?? 'Issue title',
    status: overrides.status ?? IssueStatus.Backlog,
    health: overrides.health ?? IssueHealth.Active,
    projectId: 'proj-1',
    labels: {},
    createdAt: overrides.createdAt ?? '2026-01-01T00:00:00Z',
    updatedAt: overrides.updatedAt ?? '2026-01-01T00:00:00Z',
    archivedAt: overrides.archivedAt,
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

function makeWrapper() {
  const queryClient = new QueryClient()
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        <MemoryRouter>{children}</MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('DashboardDigestWidget', () => {
  it('renders three sections (completed, failed, archived) with DigestRows when populated', () => {
    useIssuesMock.mockReturnValue({
      data: [
        makeIssue({
          id: 'i-1',
          number: 101,
          title: 'Ship digest widget',
          status: IssueStatus.Done,
          updatedAt: new Date(Date.now() - 60 * 60 * 1000).toISOString(),
        }),
        makeIssue({
          id: 'i-2',
          number: 102,
          title: 'Failing build',
          status: IssueStatus.Cancelled,
          updatedAt: new Date(Date.now() - 2 * 60 * 60 * 1000).toISOString(),
        }),
      ],
      isLoading: false,
    })
    useArchivedIssuesMock.mockReturnValue({
      data: [
        makeIssue({
          id: 'i-3',
          number: 99,
          title: 'Old cleanup issue',
          status: IssueStatus.Done,
          updatedAt: new Date(Date.now() - 5 * 24 * 60 * 60 * 1000).toISOString(),
          archivedAt: new Date(Date.now() - 3 * 24 * 60 * 60 * 1000).toISOString(),
        }),
      ],
      isLoading: false,
    })

    render(<DashboardDigestWidget />, { wrapper: makeWrapper() })

    expect(screen.getByTestId('dashboard-digest-content')).toBeInTheDocument()
    expect(screen.getByTestId('dashboard-digest-completed')).toBeInTheDocument()
    expect(screen.getByTestId('dashboard-digest-failed')).toBeInTheDocument()
    expect(screen.getByTestId('dashboard-digest-archived')).toBeInTheDocument()

    const rows = screen.getAllByTestId('digest-row')
    expect(rows).toHaveLength(3)

    expect(screen.getByText('#101')).toBeInTheDocument()
    expect(screen.getByText('Ship digest widget')).toBeInTheDocument()
    expect(screen.getByText('#102')).toBeInTheDocument()
    expect(screen.getByText('Failing build')).toBeInTheDocument()
    expect(screen.getByText('#99')).toBeInTheDocument()
    expect(screen.getByText('Old cleanup issue')).toBeInTheDocument()
  })

  it('renders a single empty-state message when all three categories resolve empty', () => {
    useIssuesMock.mockReturnValue({ data: [], isLoading: false })
    useArchivedIssuesMock.mockReturnValue({ data: [], isLoading: false })

    render(<DashboardDigestWidget />, { wrapper: makeWrapper() })

    const empty = screen.getByTestId('dashboard-digest-empty')
    expect(empty).toBeInTheDocument()
    expect(empty).toHaveTextContent('No recent activity')

    expect(screen.queryByTestId('dashboard-digest-content')).toBeNull()
    expect(screen.queryByTestId('dashboard-digest-loading')).toBeNull()
    expect(screen.queryByTestId('dashboard-digest-completed')).toBeNull()
    expect(screen.queryByTestId('dashboard-digest-failed')).toBeNull()
    expect(screen.queryByTestId('dashboard-digest-archived')).toBeNull()
  })

  it('renders a loading indicator while queries are isLoading and no empty state', () => {
    useIssuesMock.mockReturnValue({ data: undefined, isLoading: true })
    useArchivedIssuesMock.mockReturnValue({ data: undefined, isLoading: true })

    render(<DashboardDigestWidget />, { wrapper: makeWrapper() })

    const loading = screen.getByTestId('dashboard-digest-loading')
    expect(loading).toBeInTheDocument()
    expect(loading).toHaveAttribute('role', 'status')
    expect(loading).toHaveTextContent(/loading/i)

    expect(screen.queryByTestId('dashboard-digest-empty')).toBeNull()
    expect(screen.queryByTestId('dashboard-digest-content')).toBeNull()
  })

  it('renders only non-empty sections when one or more categories are empty', () => {
    useIssuesMock.mockReturnValue({
      data: [
        makeIssue({
          id: 'i-done',
          number: 5,
          title: 'Only done',
          status: IssueStatus.Done,
          updatedAt: new Date(Date.now() - 30 * 60 * 1000).toISOString(),
        }),
      ],
      isLoading: false,
    })
    useArchivedIssuesMock.mockReturnValue({ data: [], isLoading: false })

    render(<DashboardDigestWidget />, { wrapper: makeWrapper() })

    expect(screen.getByTestId('dashboard-digest-completed')).toBeInTheDocument()
    expect(screen.queryByTestId('dashboard-digest-failed')).toBeNull()
    expect(screen.queryByTestId('dashboard-digest-archived')).toBeNull()
    expect(screen.queryByTestId('dashboard-digest-empty')).toBeNull()
  })

  it('renders completed rows in most-recent-first order by completedAt', () => {
    useIssuesMock.mockReturnValue({
      data: [
        makeIssue({
          id: 'older',
          number: 10,
          title: 'Older done',
          status: IssueStatus.Done,
          completedAt: new Date(Date.now() - 6 * 60 * 60 * 1000).toISOString(),
          updatedAt: new Date(Date.now() - 6 * 60 * 60 * 1000).toISOString(),
        }),
        makeIssue({
          id: 'newer',
          number: 11,
          title: 'Newer done',
          status: IssueStatus.Done,
          completedAt: new Date(Date.now() - 30 * 60 * 1000).toISOString(),
          updatedAt: new Date(Date.now() - 30 * 60 * 1000).toISOString(),
        }),
      ],
      isLoading: false,
    })
    useArchivedIssuesMock.mockReturnValue({ data: [], isLoading: false })

    render(<DashboardDigestWidget />, { wrapper: makeWrapper() })

    const rows = screen.getAllByTestId('digest-row')
    expect(rows).toHaveLength(2)
    expect(rows[0]).toHaveAttribute('data-issue-number', '11')
    expect(rows[1]).toHaveAttribute('data-issue-number', '10')
  })
})

describe('DigestRow', () => {
  it('renders issue number, title, and relative timestamp via shared formatTimeAgo', () => {
    const updatedAt = new Date(Date.now() - 90 * 60 * 1000).toISOString()
    const issue = makeIssue({ number: 7, title: 'Row title', updatedAt })

    render(<DigestRow issue={issue} timestamp={updatedAt} />, { wrapper: makeWrapper() })

    const row = screen.getByTestId('digest-row')
    expect(row).toHaveTextContent('#7')
    expect(row).toHaveTextContent('Row title')
    expect(row).toHaveTextContent(/ago|just now|date/i)
  })

  it('renders a neutral timestamp label when the timestamp is invalid', () => {
    const issue = makeIssue({ number: 8, title: 'Invalid time row' })

    render(<DigestRow issue={issue} timestamp="not-a-date" />, { wrapper: makeWrapper() })

    const row = screen.getByTestId('digest-row')
    expect(row).toHaveTextContent('#8')
    expect(row).toHaveTextContent('Invalid time row')
    expect(row).toHaveTextContent('Unknown time')
  })

  it('navigates to /issues/<number> using useProjectPath (issue detail target)', () => {
    const issue = makeIssue({ number: 42, title: 'Click me' })

    render(<DigestRow issue={issue} timestamp={issue.updatedAt} />, { wrapper: makeWrapper() })

    const row = screen.getByTestId('digest-row')
    expect(row.getAttribute('href')).toMatch(/\/issues\/42$/)
  })
})
