import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { server } from '../../../../tests/support/msw'
import { mockIssueError, mockIssuePending, mountIssueDetail } from './_issueDetailMsw'

const projects: Project[] = [{
  id: 'proj-1',
  name: 'Project 1',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  repositories: [],
}]

function makeIssue(overrides: Record<string, unknown> = {}) {
  return {
    number: 14,
    title: 'Test Issue',
    body: '',
    status: 'backlog',
    health: 'active',
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [],
    ...overrides,
  }
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        <MemoryRouter initialEntries={['/issues/14']}>
          <Routes>
            <Route path="/issues/:number" element={<IssueDetailPage />} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

afterEach(cleanup)
mountIssueDetail({ issue: makeIssue() })

describe('IssueDetailPage initial load', () => {
  it('renders skeleton placeholders instead of a bare Loading line while the issue is loading', async () => {
    const resolve = mockIssuePending()

    renderPage()

    await waitFor(() => expect(screen.getByTestId('issue-detail-page-skeleton')).toBeTruthy())
    expect(screen.queryByText(/^Loading\.{0,3}$/)).toBeNull()

    const skeleton = screen.getByTestId('issue-detail-page-skeleton')
    expect(skeleton.querySelector('[data-slot="skeleton"]')).toBeTruthy()

    resolve(makeIssue())

    await waitFor(() => expect(screen.getByTestId('issue-detail-header')).toBeTruthy())
    expect(screen.queryByTestId('issue-detail-page-skeleton')).toBeNull()
  })
})

describe('IssueDetailPage error state', () => {
  it('renders the not-found state when the issue query returns a 404', async () => {
    mockIssueError(404, 'Issue #14 not found')
    renderPage()

    await waitFor(() => expect(screen.getByText('Page not found')).toBeTruthy())
    expect(screen.queryByTestId('error-state')).toBeNull()
    expect(screen.queryByTestId('error-state-retry')).toBeNull()
  })

  it('renders a retryable ErrorState when the issue query fails with a non-404 (transient) error', async () => {
    mockIssueError(503, 'Service temporarily unavailable')
    renderPage()

    await waitFor(() => expect(screen.getByTestId('error-state')).toBeTruthy())
    expect(screen.getByTestId('error-state-title').textContent).toBe('Failed to load issue')
    expect(screen.getByTestId('error-state-message').textContent).toBe('Service temporarily unavailable')
    const retry = screen.getByTestId('error-state-retry')
    expect(retry).toBeTruthy()
    expect(retry.textContent).toBe('Retry')
    expect(screen.queryByText('Page not found')).toBeNull()
  })

  it('triggers the useIssue refetch when the ErrorState Retry button is clicked', async () => {
    let issueFetchCount = 0
    server.use(
      http.get('*/api/projects/:projectId/issues/:number', () => {
        issueFetchCount += 1
        return HttpResponse.json(
          { success: false, error: 'Transport failed', code: 'transport_error' },
          { status: 503 },
        )
      }),
    )

    renderPage()

    await waitFor(() => expect(screen.getByTestId('error-state')).toBeTruthy())
    const initialFetchCount = issueFetchCount

    fireEvent.click(screen.getByTestId('error-state-retry'))

    await waitFor(() => expect(issueFetchCount).toBeGreaterThan(initialFetchCount))
  })

  it('renders the not-found state and not the ErrorState when 404 is returned, distinguishing it from transient errors', async () => {
    mockIssueError(404, 'Issue #14 not found')

    renderPage()

    await waitFor(() => expect(screen.getByText('Page not found')).toBeTruthy())
    expect(screen.queryByTestId('error-state')).toBeNull()
    expect(screen.queryByTestId('issue-detail-page-skeleton')).toBeNull()
  })
})
