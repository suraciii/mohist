import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { ProjectProvider } from '../../../entities/project'
import { SidebarProvider } from '@/shared/ui/components/sidebar'
import { useMswServer } from '../../../../tests/support/msw'
import { AppSidebar } from './AppSidebar'
import type { InboxItem } from '../../../entities/inbox'
import { inboxCountQueryKey } from '../../../entities/inbox'

const TEST_PROJECT = {
  id: 'test-project',
  name: 'demo',
  createdAt: '2024-01-01T00:00:00.000Z',
  updatedAt: '2024-01-01T00:00:00.000Z',
  repositories: [],
}

const AGENT_STATUS_PATH = `*/api/projects/${TEST_PROJECT.id}/agent/status`

useMswServer(
  http.get(AGENT_STATUS_PATH, () =>
    HttpResponse.json({
      success: true,
      data: { running: false, activeAgents: [], capacity: { active: 0, max: 8 } },
    }),
  ),
  http.get(`*/api/projects/${TEST_PROJECT.id}/inbox`, () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get(`*/api/projects/${TEST_PROJECT.id}/inbox/unread-count`, () =>
    HttpResponse.json({ success: true, data: { unreadCount: 0 } }),
  ),
)

function renderSidebar(initialRoute: string, initialProjectId: string | null = TEST_PROJECT.id) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={initialProjectId} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={[initialRoute]}>
          <SidebarProvider>
            <AppSidebar onCreateIssue={vi.fn()} />
            <LocationSpy />
          </SidebarProvider>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function LocationSpy() {
  const location = useLocation()
  return <div data-testid="location-spy" data-pathname={location.pathname} />
}

function getNavTestIdsInOrder(): string[] {
  return screen.getAllByTestId(/^nav-/).map((node) => node.getAttribute('data-testid') ?? '')
}

function getNavLabelsInOrder(): string[] {
  return getNavTestIdsInOrder().map((testId) => {
    const node = screen.getByTestId(testId)
    return node.textContent?.trim() ?? ''
  })
}

describe('AppSidebar primary navigation', () => {
  afterEach(() => {
    cleanup()
  })

  it('contains Dashboard and Issues entries with Dashboard preceding Issues', () => {
    renderSidebar('/demo')

    const dashboard = screen.getByTestId('nav-dashboard')
    const issues = screen.getByTestId('nav-issues')

    expect(dashboard).toBeInTheDocument()
    expect(within(dashboard).getByText('Dashboard')).toBeInTheDocument()
    expect(issues).toBeInTheDocument()
    expect(within(issues).getByText('Issues')).toBeInTheDocument()

    const dashboardIndex = getNavTestIdsInOrder().indexOf('nav-dashboard')
    const issuesIndex = getNavTestIdsInOrder().indexOf('nav-issues')
    expect(dashboardIndex).toBeGreaterThanOrEqual(0)
    expect(issuesIndex).toBeGreaterThan(dashboardIndex)
  })

  it('exposes an Insights entry that navigates to /insights via useProjectPath', () => {
    renderSidebar('/demo')

    const insights = screen.getByTestId('nav-insights')
    expect(insights).toBeInTheDocument()
    expect(within(insights).getByText('Insights')).toBeInTheDocument()

    fireEvent.click(insights)

    expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/demo/insights')
  })

  it('highlights the Insights entry on /:projectName/insights', () => {
    renderSidebar('/demo/insights')

    expect(screen.getByTestId('nav-insights')).toHaveAttribute('data-active', 'true')
  })

  it('does not contain Board or Home primary nav entries', () => {
    renderSidebar('/demo')

    expect(screen.queryByTestId('nav-board')).not.toBeInTheDocument()
    expect(screen.queryByTestId('nav-home')).not.toBeInTheDocument()

    expect(screen.queryByText('Board')).not.toBeInTheDocument()
    expect(screen.queryByText('Home')).not.toBeInTheDocument()
  })

  it('preserves the canonical navigation order: Dashboard, Insights, Issues, Inbox, Activity, Runners, Epics, Logs, Settings, Archived', () => {
    renderSidebar('/demo')

    const order = getNavTestIdsInOrder()
    expect(order).toEqual([
      'nav-dashboard',
      'nav-insights',
      'nav-issues',
      'nav-agents',
      'nav-inbox',
      'nav-activity',
      'nav-runners',
      'nav-epics',
      'nav-logs',
      'nav-settings',
      'nav-archived',
    ])
  })

  it('navigates to the project root when Dashboard is activated', () => {
    renderSidebar('/demo/issues/42')

    fireEvent.click(screen.getByTestId('nav-dashboard'))

    expect(screen.getByTestId('nav-dashboard')).toHaveAttribute('data-active', 'true')
    expect(screen.getByTestId('nav-issues')).not.toHaveAttribute('data-active', 'true')
  })

  it('navigates to /issues when Issues is activated', () => {
    renderSidebar('/demo')

    fireEvent.click(screen.getByTestId('nav-issues'))

    expect(screen.getByTestId('nav-issues')).toHaveAttribute('data-active', 'true')
  })

  it('highlights the Issues entry on both /issues and /issues/:number (existing isNavActive behavior)', () => {
    const { unmount } = renderSidebar('/demo/issues')
    expect(screen.getByTestId('nav-issues')).toHaveAttribute('data-active', 'true')
    unmount()

    renderSidebar('/demo/issues/42')
    expect(screen.getByTestId('nav-issues')).toHaveAttribute('data-active', 'true')
  })

  it('highlights the Dashboard entry on the project root', () => {
    renderSidebar('/demo')

    expect(screen.getByTestId('nav-dashboard')).toHaveAttribute('data-active', 'true')
  })

  it('highlights the Settings entry on the application-scoped /settings/ai route', () => {
    renderSidebar('/settings/ai')

    expect(screen.getByTestId('nav-settings')).toHaveAttribute('data-active', 'true')
  })

  it('highlights the Settings entry on the legacy project-scoped /:projectName/settings/:section route', () => {
    renderSidebar('/demo/settings/repositories')

    expect(screen.getByTestId('nav-settings')).toHaveAttribute('data-active', 'true')
  })

  it('navigates to /settings/ai (no project prefix) when Settings is clicked with a selected project', () => {
    renderSidebar('/demo')

    fireEvent.click(screen.getByTestId('nav-settings'))

    expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/settings/ai')
    expect(screen.getByTestId('nav-settings')).toHaveAttribute('data-active', 'true')
  })

  it('navigates to /settings/ai when Settings is clicked with no project selected', () => {
    renderSidebar('/demo', null)

    fireEvent.click(screen.getByTestId('nav-settings'))

    expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/settings/ai')
    expect(screen.getByTestId('nav-settings')).toHaveAttribute('data-active', 'true')
  })

  it('navigates Logs through the selected project route', () => {
    renderSidebar('/demo')

    fireEvent.click(screen.getByTestId('nav-logs'))

    expect(screen.getByTestId('location-spy')).toHaveAttribute('data-pathname', '/demo/logs')
    expect(screen.getByTestId('nav-logs')).toHaveAttribute('data-active', 'true')
  })

  it('renders Archived after Logs and Settings (Archived renders last)', () => {
    renderSidebar('/demo')

    const order = getNavTestIdsInOrder()
    const logsIndex = order.indexOf('nav-logs')
    const settingsIndex = order.indexOf('nav-settings')
    const archivedIndex = order.indexOf('nav-archived')

    expect(logsIndex).toBeGreaterThanOrEqual(0)
    expect(settingsIndex).toBeGreaterThan(logsIndex)
    expect(archivedIndex).toBeGreaterThan(settingsIndex)
  })

  it('exposes Dashboard and Issues labels in the rendered navigation set', () => {
    renderSidebar('/demo')

    const labels = getNavLabelsInOrder()
    expect(labels).toContain('Dashboard')
    expect(labels).toContain('Issues')
  })

  it('keeps Settings visible when no project is selected (global config is reachable)', () => {
    renderSidebar('/demo', null)

    expect(screen.getByTestId('nav-logs')).toBeInTheDocument()
    expect(screen.getByTestId('nav-settings')).toBeInTheDocument()
  })

  it('shows Settings when a project is selected', () => {
    renderSidebar('/demo')

    expect(screen.getByTestId('nav-logs')).toBeInTheDocument()
    expect(screen.getByTestId('nav-settings')).toBeInTheDocument()
  })
})

describe('AppSidebar unread inbox count badge', () => {
  function renderSidebarWithCache(initialRoute: string, inboxData: InboxItem[]) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    queryClient.setQueryData(inboxCountQueryKey(TEST_PROJECT.id), {
      unreadCount: inboxData.filter((item) => !item.isRead).length,
    })
    return render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          <MemoryRouter initialEntries={[initialRoute]}>
            <SidebarProvider>
              <AppSidebar onCreateIssue={vi.fn()} />
            </SidebarProvider>
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )
  }

  const readItem: InboxItem = {
    itemId: 'inb-1', notificationKind: 'workflow_failed', issueNumber: 1,
    issueTitle: 'Read', createdAt: '2024-01-01T00:00:00.000Z', isRead: true, isArchived: false,
    readAt: '2024-01-02T00:00:00.000Z', archivedAt: null,
  }
  const unreadItem: InboxItem = {
    itemId: 'inb-2', notificationKind: 'issue_started', issueNumber: 2,
    issueTitle: 'Unread', createdAt: '2024-01-01T00:00:00.000Z', isRead: false, isArchived: false,
    readAt: null, archivedAt: null,
  }

  it('shows a badge with the unread count when there are unread inbox items', () => {
    renderSidebarWithCache('/demo', [readItem, unreadItem])

    const badge = screen.getByTestId('nav-inbox-badge')
    expect(badge).toBeInTheDocument()
    expect(badge).toHaveTextContent('1')
  })

  it('does NOT show a badge when all inbox items are read', () => {
    renderSidebarWithCache('/demo', [readItem])

    expect(screen.queryByTestId('nav-inbox-badge')).not.toBeInTheDocument()
  })

  it('does NOT show a badge when inbox data is not yet loaded', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          <MemoryRouter initialEntries={['/demo']}>
            <SidebarProvider>
              <AppSidebar onCreateIssue={vi.fn()} />
            </SidebarProvider>
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect(screen.queryByTestId('nav-inbox-badge')).not.toBeInTheDocument()
  })

  it('updates the badge count when unread items change (live update)', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    queryClient.setQueryData(inboxCountQueryKey(TEST_PROJECT.id), { unreadCount: 1 })
    const { rerender } = render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          <MemoryRouter initialEntries={['/demo']}>
            <SidebarProvider>
              <AppSidebar onCreateIssue={vi.fn()} />
            </SidebarProvider>
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect(screen.getByTestId('nav-inbox-badge')).toHaveTextContent('1')

    queryClient.setQueryData(inboxCountQueryKey(TEST_PROJECT.id), { unreadCount: 0 })

    rerender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          <MemoryRouter initialEntries={['/demo']}>
            <SidebarProvider>
              <AppSidebar onCreateIssue={vi.fn()} />
            </SidebarProvider>
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect(screen.queryByTestId('nav-inbox-badge')).not.toBeInTheDocument()
  })
})
