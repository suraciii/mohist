// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Project } from '../../../entities/project'
import { ProjectProvider } from '../../../entities/project'
import { SettingsPage } from './SettingsPage'

const useRepositoriesMock = vi.fn()

vi.mock('../../../entities/project', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/project')>()
  return {
    ...actual,
    useRepositories: (projectId: string | undefined) => useRepositoriesMock(projectId),
    useAddRepository: () => ({ mutate: vi.fn(), isPending: false }),
    useRemoveRepository: () => ({ mutate: vi.fn(), isPending: false }),
    useSetDefaultRepository: () => ({ mutate: vi.fn(), isPending: false }),
  }
})

const projects: Project[] = [
  {
    id: 'proj-first',
    name: 'first-project',
    path: '/tmp/first',
    repositories: [
      { name: 'first', path: '/tmp/first', baseBranch: 'main', isDefault: true },
    ],
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:00Z',
  },
  {
    id: 'proj-selected',
    name: 'selected-project',
    path: '/tmp/selected',
    repositories: [
      { name: 'selected', path: '/tmp/selected', baseBranch: 'master', isDefault: true },
    ],
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:00Z',
  },
]

function renderSettings() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-selected" initialProjects={projects}>
        <MemoryRouter initialEntries={['/settings/repositories']}>
          <Routes>
            <Route path="/settings/:section" element={<SettingsPage />} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('SettingsPage', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('loads repositories for the selected project instead of the first project', () => {
    useRepositoriesMock.mockImplementation((projectId: string | undefined) => ({
      data: projectId === 'proj-selected' ? projects[1].repositories : projects[0].repositories,
      isLoading: false,
    }))

    renderSettings()

    expect(useRepositoriesMock).toHaveBeenCalledWith('proj-selected')
    expect(screen.getAllByText('selected').length).toBeGreaterThan(0)
    expect(screen.queryByText('first')).not.toBeInTheDocument()
  })
})
