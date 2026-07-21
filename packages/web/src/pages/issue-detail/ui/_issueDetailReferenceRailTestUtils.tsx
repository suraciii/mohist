// @vitest-environment jsdom
/**
 * Shared helpers for the IssueDetailPage reference-rail colocated test files.
 *
 * Module mocks and hoisted values are scoped per-file, so each `*.test.tsx`
 * declares its own mock blocks and the mock-control variables those
 * factories close over (`mockUseIssue`, `mockUseAgentStatus`, ...). Those cannot
 * be imported. This module only exports the non-mock helpers shared across the
 * reference-rail render tests:
 *   - `projects` fixture + `renderPage()` mounting <IssueDetailPage/> behind router + providers
 *   - `makeIssue()` fixture builder and `DEFAULT_RECOVERY`
 *   - `mockMatchMedia()` viewport fake + `enabledString()` for the timeline mock
 *   - DOM-order helpers `expectPreceding()` / `describeEl()`
 *   - shared rail-card / reading-flow testid arrays
 */
import { render } from '@testing-library/react'
import { expect, vi } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { getCurrentIssueFixture } from './_issueDetailMsw'

export const projects: Project[] = [
  {
    id: 'proj-1',
    name: 'Project 1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    repositories: [],
  },
]

export function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const issue = getCurrentIssueFixture()
  if (issue) {
    queryClient.setQueryDefaults(['issues', 14, 'proj-1'], { staleTime: Infinity })
    queryClient.setQueryData(['issues', 14, 'proj-1'], issue)
  }
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/issues/14']}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <Routes>
            <Route path="/issues/:number" element={<IssueDetailPage />} />
          </Routes>
        </ProjectProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

export function makeIssue(overrides: Record<string, unknown> = {}) {
  return {
    number: 14,
    title: 'Test Issue',
    body: '',
    status: 'in_progress',
    workflowStage: 'build',
    workflowStatus: 'running',
    health: 'active',
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [],
    ...overrides,
  }
}

export const DEFAULT_RECOVERY = {
  currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
  latestAttemptState: 'running',
  workflowSummaryState: 'running',
  allowedActions: ['stop'],
}

export function describeEl(el: Element): string {
  const testId = el.getAttribute('data-testid')
  return testId ? `[data-testid="${testId}"]` : el.tagName.toLowerCase()
}

export function expectPreceding(a: Element, b: Element) {
  const relationship = a.compareDocumentPosition(b)
  expect(
    (relationship & Node.DOCUMENT_POSITION_FOLLOWING) !== 0,
    `expected ${describeEl(a)} to precede ${describeEl(b)}`,
  ).toBe(true)
}

export function mockMatchMedia(narrow: boolean) {
  let matches = narrow
  const listeners = new Set<(event: MediaQueryListEvent) => void>()
  const mql = {
    get matches() {
      return matches
    },
    media: '(max-width: 1023.98px)',
    addEventListener: vi.fn((_event: string, listener: (event: MediaQueryListEvent) => void) => {
      listeners.add(listener)
    }),
    removeEventListener: vi.fn((_event: string, listener: (event: MediaQueryListEvent) => void) => {
      listeners.delete(listener)
    }),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
    onchange: null,
  }
  vi.stubGlobal('matchMedia', vi.fn(() => mql))
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: narrow ? 375 : 1280 })
  return {
    setNarrow(next: boolean) {
      matches = next
      Object.defineProperty(window, 'innerWidth', { configurable: true, value: next ? 375 : 1280 })
      const event = { matches, media: mql.media } as MediaQueryListEvent
      for (const listener of listeners) listener(event)
    },
  }
}

export const RAIL_CARD_TESTIDS = [
  'reference-rail-details',
  'reference-rail-workflow-profile',
  'reference-rail-drift',
  'reference-rail-convergence',
  'reference-rail-configuration',
  'reference-rail-prerequisites',
  'reference-rail-readiness',
] as const

export const READING_FLOW_LAST_TESTIDS = [
  'comments-section',
  'description-section',
  'commits-section',
  'diff-files-section',
] as const
