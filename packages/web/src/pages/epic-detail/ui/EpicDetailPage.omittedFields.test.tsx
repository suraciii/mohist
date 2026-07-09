// @vitest-environment jsdom
// Regression: the epic detail API omits nullable fields (startBlocker, nextIssueReason)
// when they are null. The page must tolerate their absence (undefined, not null) without
// crashing, and still identify the startable next issue. All fixture data below is
// synthetic and unrelated to any real epic/issue.
import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { EpicDetailPage } from './EpicDetailPage'
import { useMswServer } from '../../../../tests/support/msw'

vi.mock('../../../widgets/epic-dependency-graph', () => ({
  DependencyGraphWidget: () => null,
  DependencyGraphErrorBoundary: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}))

const epic = {
  id: 'epic-fixture-1',
  number: 7,
  title: 'Fixture epic',
  description: 'Fixture description',
  priority: 'p2',
  status: 'paused',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 2,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: { id: 'issue-fixture-2', number: 2, title: 'Fixture backlog issue' },
    readyToMarkDone: false,
  },
  linkedIssues: [
    { id: 'issue-fixture-1', number: 1, title: 'Fixture done issue', status: 'done', stage: 'done', health: 'done', priority: 'p2', canStart: false, prerequisiteNumbers: [], externalPrerequisites: [] },
    // backlog, startable, and startBlocker is OMITTED (the regression trigger)
    { id: 'issue-fixture-2', number: 2, title: 'Fixture backlog issue', status: 'backlog', stage: '', health: 'active', priority: 'p2', canStart: true, prerequisiteNumbers: [], externalPrerequisites: [] },
  ],
}

const HANDLERS = [
  http.get('*/api/projects/:projectId/epics/:id', () => HttpResponse.json({ success: true, data: epic })),
  http.get('*/api/projects/:projectId/epics', () => HttpResponse.json({ success: true, data: [] })),
  http.get('*/api/projects/:projectId/issues', () => HttpResponse.json({ success: true, data: [] })),
]

useMswServer(...HANDLERS)

describe('EpicDetailPage when the API omits nullable fields', () => {
  it('renders without crashing and identifies the startable next issue', async () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={qc}>
        <ProjectProvider initialProjectId="proj-fixture">
          <MemoryRouter initialEntries={['/epic/epic-fixture-1']}>
            <Routes>
              <Route path="/epic/:id" element={<EpicDetailPage />} />
              <Route path="/epics" element={<div>Epics</div>} />
            </Routes>
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect(await screen.findByText('Fixture epic')).toBeTruthy()
    // startBlocker omitted -> the backlog issue is correctly seen as startable
    expect(await screen.findByTestId('next-issue')).toHaveTextContent('#2')
  })
})
