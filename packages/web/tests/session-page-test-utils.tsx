import { TEST_PROJECT, baseRender, screen, fireEvent, renderHook } from './test-utils'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../src/entities/project/model/ProjectContext'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import React from 'react'
import type { SessionTurn, CoderSessionDetail, AgentSessionMetadata } from '../src/entities/coder-session'

export const queryClients: QueryClient[] = []

export function createMockQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  })
}

export function renderWithQueryClient(
  ui: React.ReactElement,
  initialEntry = '/issues/123/workflow/sessions/session-123',
) {
  const queryClient = createMockQueryClient()
  queryClients.push(queryClient)
  return baseRender(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={[initialEntry]}>
          <Routes>
            <Route path="/issues/:number/workflow/sessions/:sessionName" element={ui} />
            <Route path="/issues/:number/workflow/sessions/:sessionId" element={ui} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

export function renderHookWithQueryClient<T>(callback: () => T) {
  const queryClient = createMockQueryClient()
  queryClients.push(queryClient)
  return renderHook(callback, {
    wrapper: ({ children }: { children: React.ReactNode }) => (
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          {children}
        </ProjectProvider>
      </QueryClientProvider>
    ),
  })
}

export function makeTurn(overrides: Partial<SessionTurn> = {}): SessionTurn {
  return {
    id: 'turn-1',
    startedAt: '2024-01-01T10:00:00.000Z',
    completedAt: null,
    user: {
      role: 'mohist',
      text: 'Test prompt text',
      kind: 'task',
      sentAt: '2024-01-01T10:00:00.000Z',
    },
    assistant: [],
    ...overrides,
  }
}

export function convertLegacyToAgentMetadata(detail: CoderSessionDetail): AgentSessionMetadata {
  const legacy = detail.metadata
  return {
    id: detail.id,
    sessionName: legacy.sessionId,
    runtimeSessionId: detail.runtimeSessionId,
    status: legacy.status ?? detail.status,
    statusKind: legacy.statusKind,
    model: legacy.model ?? detail.model,
    stage: legacy.stage ?? detail.stage,
    title: legacy.title ?? detail.title,
    createdAt: detail.createdAt,
    completedAt: detail.completedAt,
    lastActivityAt: legacy.lastActivityAt ?? null,
    lastDataAt: legacy.lastDataAt ?? null,
    probeSentAt: legacy.probeSentAt ?? null,
    probeDeadlineAt: legacy.probeDeadlineAt ?? null,
    failureReason: legacy.failureReason ?? null,
    turnCount: legacy.turnCount ?? 0,
    changedFiles: legacy.changedFiles,
    metadata: {
      eventCount: legacy.eventCount ?? 0,
      toolCount: legacy.toolCount ?? 0,
    },
  }
}

export function getAssistantCopyButton() {
  const buttons = screen.getAllByText('Copy')
  return buttons[buttons.length - 1] as HTMLButtonElement
}

export function expandChangedFilesTool() {
  const labels = screen.getAllByText('1 file changed')
  const toggle = labels[0]?.closest('button')
  if (!toggle) throw new Error('Changed files toggle not found')
  fireEvent.click(toggle)
}
