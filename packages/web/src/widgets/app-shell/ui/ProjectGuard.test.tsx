import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { ProjectProvider } from '../../../entities/project'
import { server, useMswServer } from '../../../../tests/support/msw'
import { ProjectGuard } from './ProjectGuard'

useMswServer()

const PROJECTS_PATH = '*/api/projects'

function mockProjectsResponse(projects: unknown[] = []) {
  server.use(http.get(PROJECTS_PATH, () => HttpResponse.json({ success: true, data: projects })))
}

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
    mockProjectsResponse([])
  })

  afterEach(() => {
    cleanup()
    window.localStorage.clear()
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

  it('does NOT bypass for the project-scoped /:projectName/settings/<project-section> route', async () => {
    renderGuard('/demo/settings/repositories')

    await waitFor(() => {
      expect(screen.queryByText('No projects yet')).toBeInTheDocument()
    })
  })

  it('does NOT bypass for an unrelated project-scoped route when no project exists', async () => {
    renderGuard('/demo/issues')

    await waitFor(() => {
      expect(screen.queryByText('No projects yet')).toBeInTheDocument()
    })
  })

  it('keeps the legacy /logs bypass intact', () => {
    renderGuard('/logs')

    expect(screen.queryByText('No projects yet')).not.toBeInTheDocument()
    expect(screen.getByTestId('guard-output')).toBeInTheDocument()
  })
})
