import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { SidebarProvider } from '@/shared/ui/components/sidebar'
import { Header, type HeaderDataHooks } from './Header'

const TEST_PROJECT = {
  id: 'test-project',
  name: 'test',
  createdAt: '2025-01-01T00:00:00.000Z',
  updatedAt: '2025-01-01T00:00:00.000Z',
  repositories: [],
}

let currentEpic: { projectId: string; number: number; title: string; description: string; priority: string; status: string; createdAt: string; updatedAt: string } | undefined
let epicLoading = true

const dataHooks: HeaderDataHooks = {
  epicHook: () => ({ data: currentEpic, isLoading: epicLoading }) as never,
  agentHook: () => ({ data: undefined }) as never,
  agentStatusHook: () => ({ data: { running: false } }) as never,
}

function renderHeader(initialRoute: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={[initialRoute]}>
          <SidebarProvider>
            <Header onCreateIssue={vi.fn()} dataHooks={dataHooks} />
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
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={[initialRoute]}>
          <SidebarProvider>
            <Routes>
              <Route path={routePath} element={<Header onCreateIssue={vi.fn()} dataHooks={dataHooks} />} />
            </Routes>
          </SidebarProvider>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function mockEpic(epic: { projectId: string; number: number; title: string; description: string; priority: string; status: string; createdAt: string; updatedAt: string } | null) {
  currentEpic = epic ?? undefined
  epicLoading = false
}

describe('Header', () => {
  beforeEach(() => {
    currentEpic = undefined
    epicLoading = true
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

  it('shows Epic #<number> on epic detail route (first-segment branch)', async () => {
    mockEpic({ projectId: 'test-project', number: 7, title: 'Test', description: '', priority: 'p1', status: 'active', createdAt: '', updatedAt: '' })
    renderHeaderWithRoute('/epics/7', '/epics/:number')
    expect(await screen.findByRole('heading', { level: 1, name: 'Epic #7' })).toBeInTheDocument()
  })

  it('shows Epic #<number> on epic detail route with project prefix (section branch)', async () => {
    mockEpic({ projectId: 'test-project', number: 3, title: 'My Epic', description: '', priority: 'p2', status: 'active', createdAt: '', updatedAt: '' })
    renderHeaderWithRoute('/demo/epics/3', '/:projectName/epics/:number')
    expect(await screen.findByRole('heading', { level: 1, name: 'Epic #3' })).toBeInTheDocument()
  })

  it('shows Epic #… while epic number is loading', () => {
    renderHeaderWithRoute('/epics/7', '/epics/:number')
    expect(screen.getByRole('heading', { level: 1, name: 'Epic #\u2026' })).toBeInTheDocument()
  })

  it('resolves Epic #<number> when path segment is the epic number itself', async () => {
    mockEpic({ projectId: 'test-project', number: 12, title: 'By Number', description: '', priority: 'p1', status: 'active', createdAt: '', updatedAt: '' })
    renderHeaderWithRoute('/demo/epics/12', '/:projectName/epics/:number')
    expect(await screen.findByRole('heading', { level: 1, name: 'Epic #12' })).toBeInTheDocument()
  })

  it('shows Epics as title on epics route (unchanged)', () => {
    renderHeader('/epics')
    expect(screen.getByRole('heading', { level: 1, name: 'Epics' })).toBeInTheDocument()
  })

  it('shows issue number on issue detail route (unchanged)', () => {
    renderHeaderWithRoute('/demo/issues/42', '/:projectName/issues/:number')
    expect(screen.getByRole('heading', { level: 1, name: 'Issue #42' })).toBeInTheDocument()
  })

  it('shows Epic #<number> on project-prefixed route with production mount (outside <Routes>)', async () => {
    mockEpic({ projectId: 'test-project', number: 3, title: 'Production Mount', description: '', priority: 'p1', status: 'active', createdAt: '', updatedAt: '' })
    renderHeader('/demo/epics/3')
    expect(await screen.findByRole('heading', { level: 1, name: 'Epic #3' })).toBeInTheDocument()
  })

  it('shows Epic #<number> on the production mount (outside <Routes>)', async () => {
    mockEpic({ projectId: 'test-project', number: 7, title: 'Production Mount', description: '', priority: 'p1', status: 'active', createdAt: '', updatedAt: '' })
    renderHeader('/epics/7')
    expect(await screen.findByRole('heading', { level: 1, name: 'Epic #7' })).toBeInTheDocument()
  })

  it('shows Epic #… while loading on production mount (outside <Routes>)', () => {
    renderHeader('/demo/epics/7')
    expect(screen.getByRole('heading', { level: 1, name: 'Epic #\u2026' })).toBeInTheDocument()
  })

  it('never displays a bare "Epic #" when the epic has loaded with no number and no path segment fallback', () => {
    renderHeader('/epics')
    expect(screen.queryByRole('heading', { level: 1, name: /^Epic #$/ })).not.toBeInTheDocument()
  })
})
