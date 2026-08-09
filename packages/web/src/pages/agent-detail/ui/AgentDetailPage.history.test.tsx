import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { AgentDetailPageComponents, AgentDetailPageDataHook } from './AgentDetailPage'
import { AgentDetailPage } from './AgentDetailPage'
import type { AgentHistoryItemDto, AgentInfo } from '../../../entities/agent'

const agent: AgentInfo = {
  id: 'agent-1',
  projectId: 'proj-1',
  name: 'Test Agent',
  description: 'A test agent',
  instructions: 'Review the change.',
  agentConfig: { model: 'gpt-4', variant: 'high' },
  skills: [],
  maxConcurrentRuns: null,
  status: 'active',
  createdAt: '2026-06-01T00:00:00.000Z',
  updatedAt: '2026-06-01T00:00:00.000Z',
}

const historyItem: AgentHistoryItemDto = {
  id: 'turn-history',
  sessionId: 's-history',
  inputId: 'input-history',
  inputIds: ['input-history'],
  turnId: 'turn-history',
  jobId: 'job-history',
  task: 'Review the change',
  context: { issueNumber: 385, repository: 'suraciii/mohist', workspaceName: 'review' },
  status: 'unknown',
  outcome: 'unknown',
  result: { message: 'Completed summary' },
  startedAt: '2026-06-10T00:00:00Z',
  endedAt: '2026-06-10T01:00:00Z',
  durationMs: 3600000,
  model: 'gpt-4',
  cost: { amount: 1.25, currency: 'USD', scope: 'session' },
  workspace: 'review',
  target: null,
  bucket: 'unknown',
}

const components: AgentDetailPageComponents = {
  AgentProfileEditor: () => null,
  SubscriptionsSection: () => null,
  ConnectionsSection: () => null,
}

const dataHook: AgentDetailPageDataHook = () => ({
  agent,
  isLoading: false,
  isError: false,
  sessions: [historyItem],
  sessionsLoading: false,
  archiveAgent: { mutate: vi.fn(), isPending: false },
  unarchiveAgent: { mutate: vi.fn(), isPending: false },
  detailStatus: undefined,
  detailStatusLoading: false,
})

describe('AgentDetailPage history projection', () => {
  it('renders canonical result fields and keeps unknown rows out of Recent', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[{
          id: 'proj-1', name: 'Test', createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z', repositories: [],
        }]}>
          <MemoryRouter initialEntries={['/agents/agent-1']}>
            <Routes>
              <Route path="/agents/:agentId" element={<AgentDetailPage components={components} dataHook={dataHook} />} />
            </Routes>
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    await screen.findByTestId('session-row-s-history-turn-history')
    expect(screen.getByTestId('agent-detail-sessions')).toHaveTextContent('Unknown')
    expect(screen.getByTestId('session-result-s-history-turn-history')).toHaveTextContent('Completed summary')
    expect(screen.getByTestId('session-duration-s-history-turn-history')).toHaveTextContent('1h')
    expect(screen.getByTestId('session-cost-s-history-turn-history')).toHaveTextContent('session 1.25 USD')
    expect(screen.getByTestId('session-context-s-history-turn-history')).toHaveTextContent('#385')
    expect(screen.queryByText('Recent')).not.toBeInTheDocument()
  })
})
