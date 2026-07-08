// @vitest-environment jsdom
/**
 * Shared test harness for the EpicDetailPage colocated test files.
 *
 * Vitest hoists `vi.mock()` per-file, so each `*.test.tsx` must declare its own
 * `vi.mock(...)` / `vi.hoisted(...)` blocks (those cannot be imported). This module
 * exports everything ELSE that the page-level + subcomponent test files share:
 *   - fixture builders (`linkedIssue`, `issue`) and shared data (`epic`, `issues`)
 *   - `renderPage()` which mounts <EpicDetailPage/> behind the router + providers
 *   - DOM-query helpers for the page layout regions (action group, mobile header, ...)
 *
 * Hoisted mock objects (`mocks`, `widgetBehavior`, `mockUseNavigate`) live in each
 * test file because the `vi.mock` factories close over them.
 */
import type { ReactElement } from 'react'
import { render, screen, type RenderResult } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { LinkedIssue } from '../../../entities/epic'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../../entities/issue'
import { EpicDetailPage } from './EpicDetailPage'

export function linkedIssue(
  overrides: Pick<LinkedIssue, 'id' | 'number'> & Partial<Omit<LinkedIssue, 'id' | 'number'>>,
): LinkedIssue {
  return {
    title: 'Issue one',
    status: IssueStatus.Backlog,
    stage: WorkflowStage.Plan,
    health: IssueHealth.Active,
    priority: 'p2',
    canStart: true,
    startBlocker: null,
    prerequisiteNumbers: [],
    externalPrerequisites: [],
    ...overrides,
  }
}

export function issue(overrides: Record<string, unknown>) {
  return {
    isDraft: false,
    canStart: true,
    blocker: null,
    status: 'backlog',
    health: 'active',
    ...overrides,
  }
}

export const epic = {
  id: 'epic-12345678',
  title: 'Epic title',
  description: 'Epic description',
  priority: 'p1',
  status: 'active',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 2,
    blockedIssues: [{ id: 'issue-2', number: 2, title: 'Blocked issue', health: 'blocked' }],
    activeIssues: [],
    nextIssue: { id: 'issue-2', number: 2, title: 'Blocked issue' },
    nextIssueReason: null,
    readyToMarkDone: false,
  },
  linkedIssues: [
    linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
    linkedIssue({ id: 'issue-2', number: 2, title: 'Blocked issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, health: IssueHealth.Blocked, priority: 'p1' }),
  ],
}

export const issues = [
  issue({ id: 'issue-1', number: 1, title: 'Done issue', canStart: false, status: 'done', health: 'done' }),
  issue({ id: 'issue-2', number: 2, title: 'Blocked issue', canStart: false, status: 'in_progress', health: 'blocked' }),
  issue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
]

export function LocationProbe() {
  const location = useLocation()
  return <div data-testid="current-path">{location.pathname}{location.search}</div>
}

export function renderPage(): RenderResult & { rerenderPage: () => void } {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const ui = (): ReactElement => (
    <QueryClientProvider client={queryClient}>
      <ProjectProvider>
        <MemoryRouter initialEntries={['/epic/epic-12345678']}>
          <LocationProbe />
          <Routes>
            <Route path="/epic/:id" element={<EpicDetailPage />} />
            <Route path="/epics" element={<div>Epics</div>} />
            <Route path="/issues/:number" element={<div>Issue</div>} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>
  )
  const result = render(ui())
  return { ...result, rerenderPage: () => result.rerender(ui()) }
}

/**
 * Returns the lifecycle/action button group that contains the Edit button.
 * Shared by page-layout and primary-action tests.
 */
export function getActionGroup(): HTMLElement {
  const editButton = screen.getByTestId('edit-epic-button')
  const actionGroup = editButton.parentElement
  if (!actionGroup) throw new Error('Epic action group not found')
  return actionGroup as HTMLElement
}

export function getMobileHeaderContainer(): HTMLElement {
  const epicNumber = screen.getByTestId('epic-number')
  const container = epicNumber.closest('.flex.flex-col.gap-4')
  if (!container) throw new Error('Epic detail mobile header container not found')
  return container as HTMLElement
}

export function getEpicDetailPageContainer(): HTMLElement {
  const epicNumber = screen.getByTestId('epic-number')
  const container = epicNumber.closest('.mx-auto')
  if (!container) throw new Error('Epic detail page container not found')
  return container as HTMLElement
}

export function getTitleBlock(): HTMLElement {
  const epicNumber = screen.getByTestId('epic-number')
  const titleBlock = epicNumber.closest('.min-w-0.flex-1')
  if (!titleBlock) throw new Error('Epic title block not found')
  return titleBlock as HTMLElement
}
