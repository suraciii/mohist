import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import type { ReactNode } from 'react'
import { ProjectProvider } from '../../../entities/project'
import { TEST_PROJECT } from '../../../../tests/test-utils'
import {
  deriveRecentDigest,
  IssueStatus,
  IssueHealth,
  type Issue,
  type UseRecentDigestResult,
} from '../../../entities/issue'
import { DashboardDigestWidget } from './DashboardDigestWidget'
import { DigestRow } from './DigestRow'

let digest: UseRecentDigestResult = {
  completed: [],
  failed: [],
  archived: [],
  isLoading: false,
}

function mockIssuesResponse(issues: Issue[]) {
  digest = { ...deriveRecentDigest(issues, issues), isLoading: false }
}

function mockIssuesPending() {
  digest = { completed: [], failed: [], archived: [], isLoading: true }
}

const digestHook = () => digest

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    number: 1,
    title: 'Issue title',
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    projectId: TEST_PROJECT.id,
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

function makeWrapper() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={[TEST_PROJECT]} initialProjectId={TEST_PROJECT.id}>
        <MemoryRouter>{children}</MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>
  )
}

beforeEach(() => {
  mockIssuesResponse([])
})

afterEach(() => {
  cleanup()
})

describe('DashboardDigestWidget', () => {
  it('renders three sections (completed, failed, archived) with DigestRows when populated', async () => {
    mockIssuesResponse([
      makeIssue({
        number: 101,
        title: 'Ship digest widget',
        status: IssueStatus.Done,
        updatedAt: '2026-07-09T07:00:00Z',
      }),
      makeIssue({
        number: 102,
        title: 'Failing build',
        status: IssueStatus.Cancelled,
        updatedAt: '2026-07-09T05:00:00Z',
      }),
      makeIssue({
        number: 99,
        title: 'Old cleanup issue',
        status: IssueStatus.Done,
        updatedAt: '2026-07-04T00:00:00Z',
        archivedAt: '2026-07-06T00:00:00Z',
      }),
    ])

    render(<DashboardDigestWidget digestHook={digestHook} />, { wrapper: makeWrapper() })

    await waitFor(() => {
      expect(screen.getByTestId('dashboard-digest-content')).toBeInTheDocument()
    })
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

  it('renders a single empty-state message when all three categories resolve empty', async () => {
    render(<DashboardDigestWidget digestHook={digestHook} />, { wrapper: makeWrapper() })

    await waitFor(() => {
      expect(screen.getByTestId('dashboard-digest-empty')).toBeInTheDocument()
    })
    expect(screen.getByTestId('dashboard-digest-empty')).toHaveTextContent('No recent activity')

    expect(screen.queryByTestId('dashboard-digest-content')).toBeNull()
    expect(screen.queryByTestId('dashboard-digest-loading')).toBeNull()
    expect(screen.queryByTestId('dashboard-digest-completed')).toBeNull()
    expect(screen.queryByTestId('dashboard-digest-failed')).toBeNull()
    expect(screen.queryByTestId('dashboard-digest-archived')).toBeNull()
  })

  it('renders a loading indicator while queries are isLoading and no empty state', () => {
    mockIssuesPending()

    render(<DashboardDigestWidget digestHook={digestHook} />, { wrapper: makeWrapper() })

    const loading = screen.getByTestId('dashboard-digest-loading')
    expect(loading).toBeInTheDocument()
    expect(loading).toHaveAttribute('role', 'status')
    expect(loading).toHaveTextContent(/loading/i)

    expect(screen.queryByTestId('dashboard-digest-empty')).toBeNull()
    expect(screen.queryByTestId('dashboard-digest-content')).toBeNull()
  })

  it('renders only non-empty sections when one or more categories are empty', async () => {
    mockIssuesResponse([
      makeIssue({
        number: 5,
        title: 'Only done',
        status: IssueStatus.Done,
        updatedAt: '2026-07-09T08:30:00Z',
      }),
    ])

    render(<DashboardDigestWidget digestHook={digestHook} />, { wrapper: makeWrapper() })

    await waitFor(() => {
      expect(screen.getByTestId('dashboard-digest-completed')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('dashboard-digest-failed')).toBeNull()
    expect(screen.queryByTestId('dashboard-digest-archived')).toBeNull()
    expect(screen.queryByTestId('dashboard-digest-empty')).toBeNull()
  })

  it('renders completed rows in most-recent-first order by completedAt', async () => {
    mockIssuesResponse([
      makeIssue({
        number: 10,
        title: 'Older done',
        status: IssueStatus.Done,
        completedAt: '2026-07-09T03:00:00Z',
        updatedAt: '2026-07-09T03:00:00Z',
      }),
      makeIssue({
        number: 11,
        title: 'Newer done',
        status: IssueStatus.Done,
        completedAt: '2026-07-09T08:30:00Z',
        updatedAt: '2026-07-09T08:30:00Z',
      }),
    ])

    render(<DashboardDigestWidget digestHook={digestHook} />, { wrapper: makeWrapper() })

    const rows = await waitFor(() => screen.getAllByTestId('digest-row'))
    expect(rows).toHaveLength(2)
    expect(rows[0]).toHaveAttribute('data-issue-number', '11')
    expect(rows[1]).toHaveAttribute('data-issue-number', '10')
  })
})

describe('DigestRow', () => {
  it('renders issue number, title, and relative timestamp via shared formatTimeAgo', () => {
    const updatedAt = '2026-07-09T07:00:00Z'
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
