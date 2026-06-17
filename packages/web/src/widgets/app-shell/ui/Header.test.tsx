// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { SidebarProvider } from '@/shared/ui/components/sidebar'
import { Header } from './Header'

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

describe('Header', () => {
  afterEach(() => {
    cleanup()
  })

  it('shows Board as title on home route', () => {
    renderHeader('/')
    expect(screen.getByRole('heading', { level: 1, name: 'Board' })).toBeInTheDocument()
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

  it('shows page title and New Issue button on non-settings route (Board)', () => {
    renderHeader('/')

    expect(screen.getByRole('heading', { level: 1, name: 'Board' })).toBeInTheDocument()
    expect(screen.getByTestId('header-new-issue')).toBeInTheDocument()
  })
})
