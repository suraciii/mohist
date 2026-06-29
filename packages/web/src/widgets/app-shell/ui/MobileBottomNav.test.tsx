// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { SidebarProvider } from '@/shared/ui/components/sidebar'
import { MobileBottomNav } from './MobileBottomNav'

const TEST_PROJECT = {
  id: 'test-project',
  name: 'demo',
  createdAt: '2024-01-01T00:00:00.000Z',
  updatedAt: '2024-01-01T00:00:00.000Z',
  repositories: [],
}

function renderMobileNav(initialRoute: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={[initialRoute]}>
          <SidebarProvider>
            <MobileBottomNav />
          </SidebarProvider>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function getMobileNavTestIdsInOrder(): string[] {
  return screen
    .getAllByTestId(/^mobile-nav-/)
    .map((node) => node.getAttribute('data-testid') ?? '')
}

describe('MobileBottomNav primary navigation', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    cleanup()
  })

  it('exposes Dashboard, Issues, and Inbox destinations', () => {
    renderMobileNav('/demo')

    expect(screen.getByTestId('mobile-nav-dashboard')).toBeInTheDocument()
    expect(screen.getByTestId('mobile-nav-issues')).toBeInTheDocument()
    expect(screen.getByTestId('mobile-nav-inbox')).toBeInTheDocument()
    expect(within(screen.getByTestId('mobile-nav-dashboard')).getByText('Dashboard')).toBeInTheDocument()
    expect(within(screen.getByTestId('mobile-nav-issues')).getByText('Issues')).toBeInTheDocument()
    expect(within(screen.getByTestId('mobile-nav-inbox')).getByText('Inbox')).toBeInTheDocument()
  })

  it('places Inbox after Activity and before Epics in the rendered tab order', () => {
    renderMobileNav('/demo')

    const order = getMobileNavTestIdsInOrder()
    const activityIndex = order.indexOf('mobile-nav-activity')
    const inboxIndex = order.indexOf('mobile-nav-inbox')
    const epicsIndex = order.indexOf('mobile-nav-epics')

    expect(activityIndex).toBeGreaterThanOrEqual(0)
    expect(inboxIndex).toBeGreaterThan(activityIndex)
    expect(epicsIndex).toBeGreaterThan(inboxIndex)
  })

  it('navigates to the project root when Dashboard is activated', () => {
    renderMobileNav('/demo/issues/42')

    const dashboard = screen.getByTestId('mobile-nav-dashboard')
    fireEvent.click(dashboard)

    expect(dashboard).toHaveAttribute('data-active', 'true')
  })

  it('navigates to /issues when Issues is activated', () => {
    renderMobileNav('/demo')

    const issues = screen.getByTestId('mobile-nav-issues')
    fireEvent.click(issues)

    expect(issues).toHaveAttribute('data-active', 'true')
  })

  it('highlights the Issues tab on both /issues and /issues/:number (existing isActive behavior)', () => {
    const { unmount } = renderMobileNav('/demo/issues')
    expect(screen.getByTestId('mobile-nav-issues')).toHaveAttribute('data-active', 'true')
    unmount()

    renderMobileNav('/demo/issues/42')
    expect(screen.getByTestId('mobile-nav-issues')).toHaveAttribute('data-active', 'true')
  })

  it('navigates to /inbox when Inbox is activated', () => {
    renderMobileNav('/demo')

    const inbox = screen.getByTestId('mobile-nav-inbox')
    fireEvent.click(inbox)

    expect(inbox).toHaveAttribute('data-active', 'true')
  })

  it('highlights the Inbox tab on /inbox', () => {
    renderMobileNav('/demo/inbox')

    expect(screen.getByTestId('mobile-nav-inbox')).toHaveAttribute('data-active', 'true')
  })

  it('highlights the Dashboard tab on the project root', () => {
    renderMobileNav('/demo')

    expect(screen.getByTestId('mobile-nav-dashboard')).toHaveAttribute('data-active', 'true')
  })

  it('does not contain Board or Home mobile nav entries', () => {
    renderMobileNav('/demo')

    expect(screen.queryByTestId('mobile-nav-board')).not.toBeInTheDocument()
    expect(screen.queryByTestId('mobile-nav-home')).not.toBeInTheDocument()
  })
})
