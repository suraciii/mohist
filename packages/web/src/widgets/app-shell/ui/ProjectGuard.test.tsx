// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { ProjectGuard } from './ProjectGuard'

const projectMocks = vi.hoisted(() => ({
  useProjects: vi.fn(),
}))

vi.mock('../../../entities/project', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/project')>()
  return {
    ...actual,
    useProjects: projectMocks.useProjects,
  }
})

function renderGuard(initialPath: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider>
        <MemoryRouter initialEntries={[initialPath]}>
          <Routes>
            <Route element={<ProjectGuard />}>
              <Route
                path="*"
                element={
                  <>
                    <div data-testid="guard-output">guarded-outlet</div>
                    <PathReporter />
                  </>
                }
              />
            </Route>
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function PathReporter() {
  const location = useLocation()
  return <div data-testid="path-reporter" data-pathname={location.pathname} />
}

describe('ProjectGuard — settings routing bypass', () => {
  beforeEach(() => {
    projectMocks.useProjects.mockReturnValue({ data: [], isLoading: false })
  })

  afterEach(() => {
    cleanup()
    window.localStorage.clear()
    vi.clearAllMocks()
  })

  it('bypasses the project-existence gate on the /settings index', () => {
    renderGuard('/settings')

    expect(screen.queryByText('No projects yet')).not.toBeInTheDocument()
    expect(screen.getByTestId('guard-output')).toBeInTheDocument()
  })

  it('bypasses the project-existence gate on /settings/ai (application-scope route)', () => {
    renderGuard('/settings/ai')

    expect(screen.queryByText('No projects yet')).not.toBeInTheDocument()
    expect(screen.getByTestId('guard-output')).toBeInTheDocument()
  })

  it('bypasses the project-existence gate on /settings/<application-section> for all global sections', () => {
    for (const path of ['/settings/ai', '/settings/agent', '/settings/system', '/settings/preferences']) {
      const { unmount } = renderGuard(path)
      expect(screen.queryByText('No projects yet')).not.toBeInTheDocument()
      expect(screen.getByTestId('guard-output')).toBeInTheDocument()
      unmount()
      cleanup()
    }
  })

  it('does NOT bypass for the project-scoped /:projectName/settings/<project-section> route', () => {
    renderGuard('/demo/settings/repositories')

    expect(screen.queryByText('No projects yet')).toBeInTheDocument()
  })

  it('does NOT bypass for an unrelated project-scoped route when no project exists', () => {
    renderGuard('/demo/issues')

    expect(screen.queryByText('No projects yet')).toBeInTheDocument()
  })

  it('keeps the legacy /logs bypass intact', () => {
    renderGuard('/logs')

    expect(screen.queryByText('No projects yet')).not.toBeInTheDocument()
    expect(screen.getByTestId('guard-output')).toBeInTheDocument()
  })
})