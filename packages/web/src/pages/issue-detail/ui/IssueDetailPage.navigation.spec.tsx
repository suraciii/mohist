import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { HttpResponse, http } from 'msw'
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { ProjectProvider, type Project } from '../../../entities/project'
import type { EventTimelinePanelProps } from '../../../widgets/issue-event-timeline'
import { server } from '../../../../tests/support/msw'
import { IssueDetailPage } from './IssueDetailPage'
import { mockArtifactsError, mockIssue, mountIssueDetail } from './_issueDetailMsw'

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
    title: 'Addressable issue',
    body: 'Issue description',
    status: 'backlog',
    health: 'active',
    projectId: 'proj-1',
    projectName: 'Project 1',
    workflowRunId: null,
    workflowStage: null,
    workflowStatus: null,
    labels: {},
    priority: 'p2',
    comments: [],
    children: [],
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  }
}

function TimelinePanel({ enabled }: EventTimelinePanelProps) {
  return <div data-testid="test-timeline" data-enabled={enabled ? 'true' : 'false'}>Timeline ready</div>
}

function LocationProbe() {
  const location = useLocation()
  const navigate = useNavigate()
  return (
    <>
      <output data-testid="location-value">{location.pathname}{location.search}{location.hash}</output>
      <button type="button" onClick={() => navigate(-1)}>History back</button>
      <button type="button" onClick={() => navigate(1)}>History forward</button>
    </>
  )
}

function renderPage(route: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[route]}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <Routes>
            <Route
              path="/:projectName/issues/:number"
              element={(
                <>
                  <IssueDetailPage components={{ EventTimelinePanel: TimelinePanel }} />
                  <LocationProbe />
                </>
              )}
            />
          </Routes>
        </ProjectProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

mountIssueDetail({ issue: makeIssue() })

beforeEach(() => {
  mockIssue(makeIssue())
})

afterEach(() => cleanup())

describe('IssueDetailPage fragment navigation', () => {
  it.each([
    ['workflow', 'workflow'],
    ['artifacts', 'artifacts'],
    ['comments', 'comments'],
  ])('reveals direct #%s after the issue renders', async (fragment, id) => {
    const scrollIntoView = vi.spyOn(Element.prototype, 'scrollIntoView')
    renderPage(`/project-scope/issues/14?from=notification#${fragment}`)

    await waitFor(() => expect(document.getElementById(id)).not.toBeNull())
    await waitFor(() => expect(scrollIntoView).toHaveBeenCalled())
    if (fragment === 'artifacts') expect(document.querySelectorAll('#artifacts')).toHaveLength(1)
    expect(screen.getByTestId('location-value')).toHaveTextContent(`/project-scope/issues/14?from=notification#${fragment}`)
  })

  it('opens and enables Activity directly from the URL without a trigger click', async () => {
    renderPage('/project-scope/issues/14?from=notification#activity')

    expect(await screen.findByTestId('activity-dialog-content')).toHaveAttribute('id', 'activity')
    expect(screen.getByTestId('test-timeline')).toHaveAttribute('data-enabled', 'true')
    expect(screen.getByTestId('location-value')).toHaveTextContent('/project-scope/issues/14?from=notification#activity')
  })

  it('exposes canonical same-issue links while preserving pathname and search', async () => {
    renderPage('/project-scope/issues/14?from=notification')

    const nav = await screen.findByRole('navigation', { name: 'Issue sections' })
    expect(nav.querySelector('a[href="/project-scope/issues/14?from=notification#workflow"]')).not.toBeNull()
    expect(nav.querySelector('a[href="/project-scope/issues/14?from=notification#artifacts"]')).not.toBeNull()
    expect(nav.querySelector('a[href="/project-scope/issues/14?from=notification#activity"]')).not.toBeNull()
    expect(nav.querySelector('a[href="/project-scope/issues/14?from=notification#comments"]')).not.toBeNull()
  })

  it('waits for asynchronous issue content before revealing the requested target', async () => {
    const scrollIntoView = vi.spyOn(Element.prototype, 'scrollIntoView')
    let resolveIssue!: (response: Response) => void
    const issueResponse = new Promise<Response>((resolve) => {
      resolveIssue = resolve
    })
    server.use(http.get('*/api/projects/:projectId/issues/:number', () => issueResponse))

    renderPage('/project-scope/issues/14#comments')
    expect(screen.getByText('Loading...')).toBeInTheDocument()
    expect(scrollIntoView).not.toHaveBeenCalled()

    await waitFor(() => expect(resolveIssue).toBeTypeOf('function'), { timeout: 5000 })
    resolveIssue?.(HttpResponse.json({ success: true, data: makeIssue() }))
    await waitFor(() => expect(scrollIntoView).toHaveBeenCalledTimes(1))
    expect(scrollIntoView.mock.instances[0]).toHaveAttribute('id', 'comments')
  })

  it('changes fragments in place and browser back/forward controls Activity visibility', async () => {
    const scrollIntoView = vi.spyOn(Element.prototype, 'scrollIntoView')
    renderPage('/project-scope/issues/14?scope=kept#workflow')
    await waitFor(() => expect(scrollIntoView).toHaveBeenCalled())

    fireEvent.click(await screen.findByTestId('activity-entry'))
    expect(await screen.findByTestId('activity-dialog-content')).toBeInTheDocument()
    expect(screen.getByTestId('location-value')).toHaveTextContent('/project-scope/issues/14?scope=kept#activity')

    fireEvent.click(screen.getByText('History back'))
    await waitFor(() => expect(screen.queryByTestId('activity-dialog-content')).toBeNull())
    expect(screen.getByTestId('location-value')).toHaveTextContent('/project-scope/issues/14?scope=kept#workflow')

    fireEvent.click(screen.getByText('History forward'))
    expect(await screen.findByTestId('activity-dialog-content')).toBeInTheDocument()
    expect(screen.getByTestId('location-value')).toHaveTextContent('/project-scope/issues/14?scope=kept#activity')

    fireEvent.keyDown(screen.getByTestId('activity-dialog-content'), { key: 'Escape' })
    await waitFor(() => expect(screen.queryByTestId('activity-dialog-content')).toBeNull())
    expect(screen.getByTestId('location-value')).toHaveTextContent('/project-scope/issues/14?scope=kept')
  })

  it('assigns the sole artifacts target to unavailable approval evidence', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'check',
      workflowStatus: 'paused',
      workflowRunId: 'wr-1',
      health: 'paused',
      approvalState: { status: 'awaiting', stage: 'check', requestedAt: '2026-01-01T00:00:00Z' },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: null,
        workflowSummaryState: 'awaiting-approval',
        allowedActions: ['approve', 'reject'],
      },
    }))
    mockArtifactsError()
    renderPage('/project-scope/issues/14#artifacts')

    const evidence = await screen.findByTestId('approval-review-evidence')
    expect(evidence).toHaveAttribute('id', 'artifacts')
    expect(document.querySelectorAll('#artifacts')).toHaveLength(1)
    expect(screen.queryByTestId('latest-artifacts-panel')).toBeNull()
    expect(await screen.findByText('Failed to load artifact list.')).toBeInTheDocument()
  })

  it('leaves unknown fragments usable without revealing an unrelated target', async () => {
    const scrollIntoView = vi.spyOn(Element.prototype, 'scrollIntoView')
    renderPage('/project-scope/issues/14#unknown')

    expect(await screen.findByRole('heading', { name: 'Addressable issue' })).toBeInTheDocument()
    expect(scrollIntoView).not.toHaveBeenCalled()
    expect(screen.queryByTestId('activity-dialog-content')).toBeNull()
  })

  it('fails safely when a composite parent has no requested workflow destination', async () => {
    const scrollIntoView = vi.spyOn(Element.prototype, 'scrollIntoView')
    mockIssue(makeIssue({
      children: [{ number: 15, title: 'Child', status: 'backlog', health: 'active', repositoryName: null }],
      childIssuesSummary: { count: 1, doneCount: 0, blockedCount: 0 },
    }))
    renderPage('/project-scope/issues/14#workflow')

    expect(await screen.findByRole('heading', { name: 'Addressable issue' })).toBeInTheDocument()
    expect(document.getElementById('workflow')).toBeNull()
    expect(document.getElementById('artifacts')).toBeNull()
    expect(screen.queryByTestId('activity-entry')).toBeNull()
    expect(scrollIntoView).not.toHaveBeenCalled()
    expect(screen.getByRole('link', { name: 'Comments' })).toHaveAttribute('href', '/project-scope/issues/14#comments')
  })
})
