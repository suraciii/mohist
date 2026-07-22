// @vitest-environment jsdom
import { createElement, useEffect, type ReactElement } from 'react'
import { render, screen, type RenderResult } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { EpicDetail, LinkedIssue } from '../../../entities/epic'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../../entities/issue'
import { issueListKeys } from '../../../entities/issue/api/query-keys'
import { EpicDetailPage, type EpicDetailPageComponents, type EpicDetailPageDependencies } from './EpicDetailPage'
import {
  DependencyGraphErrorBoundary,
  type DependencyGraphWidgetProps,
} from '../../../widgets/epic-dependency-graph'

export type DependencyGraphTestMode = 'default' | 'empty' | 'error'

function hasCycle(linkedIssues: { number: number; prerequisiteNumbers: number[] }[]): boolean {
  const prerequisitesByIssue = new Map<number, number[]>()
  for (const issue of linkedIssues) {
    prerequisitesByIssue.set(issue.number, issue.prerequisiteNumbers ?? [])
  }
  const visited = new Set<number>()
  const visiting = new Set<number>()

  function visit(issueNumber: number): boolean {
    if (visiting.has(issueNumber)) return true
    if (visited.has(issueNumber)) return false
    visited.add(issueNumber)
    visiting.add(issueNumber)
    for (const prerequisite of prerequisitesByIssue.get(issueNumber) ?? []) {
      if (visit(prerequisite)) return true
    }
    visiting.delete(issueNumber)
    return false
  }

  for (const issueNumber of prerequisitesByIssue.keys()) {
    if (visit(issueNumber)) return true
  }
  return false
}

export function createDependencyGraphTestComponents(
  readMode: () => DependencyGraphTestMode,
): EpicDetailPageComponents {
  function DependencyGraphWidget(props: DependencyGraphWidgetProps) {
    const mode = readMode()
    const cyclic = hasCycle(props.linkedIssues)

    useEffect(() => {
      if (mode === 'empty') {
        props.onRenderabilityChange?.({ renderable: false, reason: 'empty' })
      } else if (mode === 'default') {
        props.onRenderabilityChange?.(
          cyclic
            ? { renderable: false, reason: 'cyclic' }
            : { renderable: true, reason: null },
        )
      }
    }, [mode, cyclic, props.onRenderabilityChange])

    if (mode === 'error') {
      throw new Error('Simulated render error from DependencyGraphWidget')
    }
    if (mode !== 'default' || cyclic) return null
    return createElement('div', {
      'data-testid': 'epic-dep-graph-canvas',
      className: 'h-[560px] w-full min-w-[640px] rounded-lg border bg-background',
    })
  }

  return {
    DependencyGraphErrorBoundary,
    DependencyGraphWidget,
  }
}

export function linkedIssue(
  overrides: Pick<LinkedIssue, 'number'> & Partial<Omit<LinkedIssue, 'number'>>,
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
  projectId: 'proj-1',
  number: 123,
  title: 'Epic title',
  description: 'Epic description',
  priority: 'p1',
  status: 'active',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 2,
    blockedIssues: [{ number: 2, title: 'Blocked issue', health: 'blocked' }],
    activeIssues: [],
    nextIssue: { number: 2, title: 'Blocked issue' },
    nextIssueReason: null,
    readyToMarkDone: false,
  },
  linkedIssues: [
    linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
    linkedIssue({ number: 2, title: 'Blocked issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, health: IssueHealth.Blocked, priority: 'p1' }),
  ],
}

export const issues = [
  issue({ number: 1, title: 'Done issue', canStart: false, status: 'done', health: 'done' }),
  issue({ number: 2, title: 'Blocked issue', canStart: false, status: 'in_progress', health: 'blocked' }),
  issue({ number: 3, title: 'Candidate issue' }),
]

export function LocationProbe() {
  const location = useLocation()
  return <div data-testid="current-path">{location.pathname}{location.search}</div>
}

export interface EpicDetailRenderOptions {
  components?: Partial<EpicDetailPageComponents>
  dependencies?: Partial<EpicDetailPageDependencies>
  epic?: EpicDetail
  issues?: unknown[]
}

export function renderPage(options: EpicDetailRenderOptions = {}): RenderResult & { rerenderPage: () => void } {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const epicNumber = options.epic?.number ?? epic.number
  const eventsQueryKey = ['epics', 'proj-1', epicNumber, 'events']
  queryClient.setQueryDefaults(eventsQueryKey, { staleTime: Infinity })
  queryClient.setQueryData(eventsQueryKey, [])
  if (options.epic) {
    queryClient.setQueryDefaults(['epics', 'proj-1', options.epic.number], { staleTime: Infinity })
    queryClient.setQueryData(['epics', 'proj-1', options.epic.number], options.epic)
  }
  if (options.issues) {
    const issuesQueryKey = issueListKeys.list({ projectId: 'proj-1' })
    queryClient.setQueryDefaults(issuesQueryKey, { staleTime: Infinity })
    queryClient.setQueryData(issuesQueryKey, options.issues)
  }
  const ui = (): ReactElement => (
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1">
        <MemoryRouter initialEntries={[`/epic/${epicNumber}`]}>
          <LocationProbe />
          <Routes>
            <Route
              path="/epic/:number"
              element={(
                <EpicDetailPage
                  components={options.components}
                  dependencies={options.dependencies}
                />
              )}
            />
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
