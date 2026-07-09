// @vitest-environment jsdom
// Regression: the epic detail API omits nullable fields (startBlocker, nextIssueReason)
// when they are null. The page must tolerate their absence (undefined, not null) without
// crashing, and still identify the startable next issue. All fixture data below is
// synthetic and unrelated to any real epic/issue.
import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { EpicDetailPage } from './EpicDetailPage'

const mocks = vi.hoisted(() => ({
  useEpic: vi.fn(),
  useIssues: vi.fn(),
  useAddEpicIssue: vi.fn(),
  useRemoveEpicIssue: vi.fn(),
  useStartIssue: vi.fn(),
  useStartEpic: vi.fn(),
  useMarkEpicDone: vi.fn(),
  useCloseEpic: vi.fn(),
  useUpdateEpic: vi.fn(),
  usePauseEpic: vi.fn(),
  useResumeEpic: vi.fn(),
}))

vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  useIssues: mocks.useIssues,
}))
vi.mock('../../../entities/epic', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/epic')>()
  return {
    ...actual,
    useEpic: mocks.useEpic,
    useAddEpicIssue: mocks.useAddEpicIssue,
    useRemoveEpicIssue: mocks.useRemoveEpicIssue,
    useStartIssue: mocks.useStartIssue,
    useStartEpic: mocks.useStartEpic,
    useMarkEpicDone: mocks.useMarkEpicDone,
    useCloseEpic: mocks.useCloseEpic,
    useUpdateEpic: mocks.useUpdateEpic,
    usePauseEpic: mocks.usePauseEpic,
    useResumeEpic: mocks.useResumeEpic,
  }
})
vi.mock('../../../widgets/epic-dependency-graph', () => ({
  DependencyGraphWidget: () => null,
  DependencyGraphErrorBoundary: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}))

describe('EpicDetailPage when the API omits nullable fields', () => {
  it('renders without crashing and identifies the startable next issue', () => {
    // Fixture mimicking the API shape where nullable fields (startBlocker, nextIssueReason)
    // are absent because the server serializes them as omitted when null.
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

    mocks.useEpic.mockReturnValue({ data: epic, isLoading: false })
    mocks.useIssues.mockReturnValue({ data: [] })
    const mut = () => ({ mutate: vi.fn(), isPending: false, isError: false })
    mocks.useAddEpicIssue.mockReturnValue(mut())
    mocks.useRemoveEpicIssue.mockReturnValue(mut())
    mocks.useStartIssue.mockReturnValue(mut())
    mocks.useStartEpic.mockReturnValue(mut())
    mocks.useMarkEpicDone.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useCloseEpic.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useUpdateEpic.mockReturnValue(mut())
    mocks.usePauseEpic.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useResumeEpic.mockReturnValue({ mutate: vi.fn(), isPending: false })

    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const { getByText } = render(
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

    expect(getByText('Fixture epic')).toBeTruthy()
    // startBlocker omitted -> the backlog issue is correctly seen as startable
    expect(screen.getByTestId('next-issue').textContent).toContain('#2')
  })
})
