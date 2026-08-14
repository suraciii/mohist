// @vitest-environment jsdom
import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { EpicStatus } from '../../../entities/epic'
import { EpicListPage } from './EpicListPage'

export function LocationProbe() {
  const location = useLocation()
  return (
    <div data-testid="current-path">
      {location.pathname}
      {location.search}
    </div>
  )
}

let nextEpicNumber = 100

export function makeEpic(overrides: Record<string, unknown>) {
  const number = typeof overrides.number === 'number' ? overrides.number : nextEpicNumber++
  return {
    projectId: 'proj-1',
    number,
    title: 'Epic',
    description: 'desc',
    priority: 'p1',
    status: EpicStatus.Idle,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    progress: {
      deliveredCount: 0,
      totalIssueCount: 0,
      blockedIssues: [],
      activeIssues: [],
      nextIssue: null,
      nextIssueReason: null,
      readyToMarkDone: false,
    },
    ...overrides,
  }
}

export const runningEpic = makeEpic({
  number: 1,
  title: 'Running Epic',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 3,
    blockedIssues: [],
    activeIssues: [{ number: 2, title: 'Continue work', health: 'active' }],
    nextIssue: { number: 3, title: 'Queued next' },
    nextIssueReason: 'Waiting for #2 to complete',
    readyToMarkDone: false,
  },
})

export const readyToStartEpic = makeEpic({
  number: 2,
  title: 'Ready To Start Epic',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 3,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: { number: 3, title: 'Start me' },
    nextIssueReason: null,
    readyToMarkDone: false,
  },
})

export const waitingBlockedEpic = makeEpic({
  number: 3,
  title: 'Waiting Epic',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 3,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: null,
    nextIssueReason: 'Draft blocked on review',
    readyToMarkDone: false,
  },
})

export const idleReadyEpic = makeEpic({
  number: 4,
  title: 'Idle Ready Epic',
  progress: {
    deliveredCount: 3,
    totalIssueCount: 3,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: null,
    nextIssueReason: null,
    readyToMarkDone: true,
  },
})

export const idleEmptyEpic = makeEpic({
  number: 5,
  title: 'Empty Epic',
  progress: {
    deliveredCount: 0,
    totalIssueCount: 0,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: null,
    nextIssueReason: null,
    readyToMarkDone: false,
  },
})

export const doneEpic = makeEpic({
  number: 6,
  title: 'Done Epic',
  status: EpicStatus.Done,
  progress: {
    deliveredCount: 2,
    totalIssueCount: 2,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: null,
    nextIssueReason: null,
    readyToMarkDone: true,
  },
})

export const closedEpic = makeEpic({
  number: 7,
  title: 'Closed Epic',
  status: EpicStatus.Closed,
  progress: {
    deliveredCount: 2,
    totalIssueCount: 2,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: null,
    nextIssueReason: null,
    readyToMarkDone: false,
  },
})

export function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1">
        <MemoryRouter initialEntries={['/epics']}>
          <LocationProbe />
          <Routes>
            <Route path="/epics" element={<EpicListPage />} />
            <Route path="/epics/:number" element={<div>Epic Detail</div>} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

export async function waitForList() {
  const sections = [
    'epic-section-running',
    'epic-section-ready',
    'epic-section-waiting',
    'epic-section-idle',
    'epic-section-done',
    'epic-section-closed',
    'epic-section-paused',
  ]
  await Promise.any(sections.map((id) => screen.findByTestId(id, {}, { timeout: 5000 })))
}
