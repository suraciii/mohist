// @vitest-environment jsdom
/**
 * Shared fixtures + render helpers for the kanban-board-query colocated component test files.
 *
 * Module mocks and hoisted values are scoped per-file, so each `*.test.tsx` declares
 * its own mock blocks (those cannot be imported). This module exports the
 * non-mock helpers shared across the component render tests:
 *   - `makeIssue` / `makeIssues` (also re-used by the pure-query tests)
 *   - `mockAgentStatus`
 *   - `renderBoard()` mounting <KanbanBoard/> behind QueryClient + MemoryRouter
 *
 * The shared `beforeEach`/`afterEach` (window.location reset + cleanup) live in each
 * test file because they reference file-local mock state.
 */
import type { ReactNode } from 'react'
import { render } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { ProjectProvider } from '../../../entities/project'
import { server } from '../../../../tests/support/msw'
import { KanbanBoard } from './KanbanBoard'
import type { AgentStatus } from '../../../entities/agent'
import { IssueStatus, IssueHealth, type Issue } from '../../../entities/issue'

export const RUNNERS_PATH = '*/api/projects/:projectId/runners'

function defaultRunnersHandler() {
  return http.get(RUNNERS_PATH, () => HttpResponse.json({ success: true, data: { runners: [] } }))
}

export function runnerRowsHandler(rows: Array<Record<string, unknown>>) {
  return http.get(RUNNERS_PATH, () => HttpResponse.json({ success: true, data: { runners: rows } }))
}

export function renderBoard(ui: ReactNode): ReturnType<typeof render> {
  server.use(defaultRunnersHandler())
  const queryClient = new QueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[]}>
        <MemoryRouter>
          {ui}
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

export function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    number: 1,
    title: 'Test Issue',
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    projectId: 'proj-1',
    labels: {},
    priority: 'p2',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

export function makeIssues(count: number, overrides: Partial<Issue> = {}): Issue[] {
  return Array.from({ length: count }, (_, i) =>
    makeIssue({
      number: i + 1,
      title: `Issue ${i + 1}`,
      ...overrides,
    }),
  )
}

export const mockAgentStatus: AgentStatus = {
  running: false,
  issueNumber: null,
  activeAgents: [],
  capacity: { active: 0, max: 2 },
}

export { KanbanBoard }
