// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { SidebarProvider } from '@/shared/ui/components/sidebar'
import { AppSidebar } from './AppSidebar'

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

const projects: Project[] = [
  {
    id: 'proj-selected',
    name: 'audit-test-1',
    repositories: [],
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  },
]

function renderSidebar(initialProjectId: string | null) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={initialProjectId} initialProjects={projects}>
        <MemoryRouter initialEntries={['/']}>
          <SidebarProvider>
            <AppSidebar onCreateIssue={vi.fn()} />
          </SidebarProvider>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('AppSidebar', () => {
  afterEach(() => {
    cleanup()
    window.localStorage.clear()
  })

  it('hides Settings while keeping Logs visible when no project is selected', () => {
    renderSidebar(null)

    expect(screen.getByTestId('nav-logs')).toBeInTheDocument()
    expect(screen.queryByTestId('nav-settings')).not.toBeInTheDocument()
  })

  it('shows Settings when a project is selected', () => {
    renderSidebar('proj-selected')

    expect(screen.getByTestId('nav-logs')).toBeInTheDocument()
    expect(screen.getByTestId('nav-settings')).toBeInTheDocument()
  })
})
