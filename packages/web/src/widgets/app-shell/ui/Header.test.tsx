// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { SidebarProvider } from '@/shared/ui/components/sidebar'
import { Header } from './Header'

const epicMocks = vi.hoisted(() => ({
  useEpic: vi.fn(),
}))

vi.mock('../../../entities/project', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/project')>()
  return {
    ...actual,
    useDeleteProject: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
  }
})

vi.mock('../../../entities/agent', () => ({
  useAgentStatus: () => ({ data: { running: false, activeAgents: [], capacity: { active: 0, max: 8 } } }),
}))

vi.mock('../../../entities/epic', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/epic')>()
  return {
    ...actual,
    useEpic: epicMocks.useEpic,
  }
})

function renderHeader(initialRoute: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider>
        <MemoryRouter initialEntries={[initialRoute]}>
          <SidebarProvider>
            <Header onCreateIssue={vi.fn()} />
          </SidebarProvider>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function renderHeaderWithRoute(initialRoute: string, routePath: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider>
        <MemoryRouter initialEntries={[initialRoute]}>
          <SidebarProvider>
            <Routes>
              <Route path={routePath} element={<Header onCreateIssue={vi.fn()} />} />
            </Routes>
          </SidebarProvider>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('Header', () => {
  beforeEach(() => {
    epicMocks.useEpic.mockReturnValue({ data: undefined, isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('shows Dashboard as title on home route', () => {
    renderHeader('/')
    expect(screen.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeInTheDocument()
  })

  it('shows Issues as title on /issues route', () => {
    renderHeader('/issues')
    expect(screen.getByRole('heading', { level: 1, name: 'Issues' })).toBeInTheDocument()
  })

  it('shows issue number on project-scoped issue detail route', () => {
    renderHeaderWithRoute('/demo/issues/42', '/:projectName/issues/:number')
    expect(screen.getByRole('heading', { level: 1, name: 'Issue #42' })).toBeInTheDocument()
  })

  it('shows Epics as title on epics route', () => {
    renderHeader('/epics')
    expect(screen.getByRole('heading', { level: 1, name: 'Epics' })).toBeInTheDocument()
  })

  it('shows Activity as title on activity route', () => {
    renderHeader('/activity')
    expect(screen.getByRole('heading', { level: 1, name: 'Activity' })).toBeInTheDocument()
  })

  it('shows Logs as title on logs route', () => {
    renderHeader('/logs')
    expect(screen.getByRole('heading', { level: 1, name: 'Logs' })).toBeInTheDocument()
  })

  it('hides page title and New Issue button on settings route, keeps SidebarTrigger', () => {
    renderHeader('/settings/ai')

    expect(screen.queryByRole('heading', { level: 1 })).not.toBeInTheDocument()
    expect(screen.queryByTestId('header-new-issue')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /toggle sidebar/i })).toBeInTheDocument()
  })

  it('hides page title and New Issue button on project-scoped settings route', () => {
    renderHeader('/audit-test-1/settings/ai')

    expect(screen.queryByRole('heading', { level: 1 })).not.toBeInTheDocument()
    expect(screen.queryByTestId('header-new-issue')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /toggle sidebar/i })).toBeInTheDocument()
  })

  it('shows page title and New Issue button on non-settings route (Dashboard)', () => {
    renderHeader('/')

    expect(screen.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeInTheDocument()
    expect(screen.getByTestId('header-new-issue')).toBeInTheDocument()
  })

  it('shows Epic #<number> on epic detail route (first-segment branch)', () => {
    epicMocks.useEpic.mockReturnValue({
      data: { id: 'epic-99', number: 7, title: 'Test', description: '', priority: 'p1', status: 'active', createdAt: '', updatedAt: '' },
      isLoading: false,
    })
    renderHeaderWithRoute('/epics/epic-99', '/epics/:id')
    expect(screen.getByRole('heading', { level: 1, name: 'Epic #7' })).toBeInTheDocument()
  })

  it('shows Epic #<number> on epic detail route with project prefix (section branch)', () => {
    epicMocks.useEpic.mockReturnValue({
      data: { id: 'epic-42', number: 3, title: 'My Epic', description: '', priority: 'p2', status: 'active', createdAt: '', updatedAt: '' },
      isLoading: false,
    })
    renderHeaderWithRoute('/demo/epics/epic-42', '/:projectName/epics/:id')
    expect(screen.getByRole('heading', { level: 1, name: 'Epic #3' })).toBeInTheDocument()
  })

  it('shows Epic #… while epic number is loading', () => {
    epicMocks.useEpic.mockReturnValue({
      data: undefined,
      isLoading: true,
    })
    renderHeaderWithRoute('/epics/epic-loading', '/epics/:id')
    expect(screen.getByRole('heading', { level: 1, name: 'Epic #\u2026' })).toBeInTheDocument()
  })

  it('resolves Epic #<number> when path segment is the epic number itself', () => {
    epicMocks.useEpic.mockReturnValue({
      data: { id: 'epic-something', number: 12, title: 'By Number', description: '', priority: 'p1', status: 'active', createdAt: '', updatedAt: '' },
      isLoading: false,
    })
    renderHeaderWithRoute('/demo/epics/12', '/:projectName/epics/:id')
    expect(screen.getByRole('heading', { level: 1, name: 'Epic #12' })).toBeInTheDocument()
  })

  it('shows Epics as title on epics route (unchanged)', () => {
    renderHeader('/epics')
    expect(screen.getByRole('heading', { level: 1, name: 'Epics' })).toBeInTheDocument()
  })

  it('shows issue number on issue detail route (unchanged)', () => {
    renderHeaderWithRoute('/demo/issues/42', '/:projectName/issues/:number')
    expect(screen.getByRole('heading', { level: 1, name: 'Issue #42' })).toBeInTheDocument()
  })
})
