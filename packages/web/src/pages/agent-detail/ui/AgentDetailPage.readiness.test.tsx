import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { AgentInfo, AgentStatusDetailResponse } from '../../../entities/agent'
import {
  AgentDetailPage,
  type AgentDetailPageComponents,
  type AgentDetailPageData,
  type AgentDetailPageDataHook,
} from './AgentDetailPage'

const state: {
  agent: AgentInfo
  detailStatus: AgentStatusDetailResponse | undefined
} = {
  agent: makeAgent(),
  detailStatus: undefined,
}

const components: AgentDetailPageComponents = {
  AgentProfileEditor: () => null,
  SubscriptionsSection: () => null,
  ConnectionsSection: () => null,
}

const dataHook: AgentDetailPageDataHook = () => ({
  agent: state.agent,
  isLoading: false,
  isError: false,
  sessions: [],
  sessionsLoading: false,
  archiveAgent: { mutate: vi.fn(), isPending: false } as AgentDetailPageData['archiveAgent'],
  unarchiveAgent: { mutate: vi.fn(), isPending: false } as AgentDetailPageData['unarchiveAgent'],
  detailStatus: state.detailStatus,
  detailStatusLoading: false,
})

function makeAgent(overrides: Partial<AgentInfo> = {}): AgentInfo {
  return {
    id: 'agent-1',
    projectId: 'proj-1',
    name: 'Test Agent',
    description: 'A test agent',
    instructions: 'You are a helpful assistant.',
    agentConfig: { model: 'gpt-4', variant: 'high' },
    skills: ['code'],
    maxConcurrentRuns: null,
    status: 'active',
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  }
}

function renderPage() {
  return render(
    <ProjectProvider
      initialProjectId="proj-1"
      initialProjects={[
        {
          id: 'proj-1',
          name: 'Test',
          createdAt: '2026-01-01T00:00:00.000Z',
          updatedAt: '2026-01-01T00:00:00.000Z',
          repositories: [],
        },
      ]}
    >
      <MemoryRouter initialEntries={['/test/agents/agent-1']}>
        <Routes>
          <Route
            path="/:projectName/agents/:agentId"
            element={<AgentDetailPage components={components} dataHook={dataHook} />}
          />
        </Routes>
      </MemoryRouter>
    </ProjectProvider>,
  )
}

describe('AgentDetailPage executability and availability', () => {
  beforeEach(() => {
    state.agent = makeAgent()
    state.detailStatus = undefined
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders not-configured with the server-provided actionable entry', () => {
    state.agent = makeAgent({
      executability: {
        state: 'not-configured',
        gaps: [
          {
            code: 'instructions-missing',
            message: 'Instructions are missing.',
            nextAction: 'Add instructions in Agent settings.',
            fixEntryPoint: { label: 'Agent settings', path: '/agents/agent-1', command: 'mo agent edit agent-1' },
          },
        ],
        pendingLaunchNote: null,
      },
    })
    renderPage()

    expect(screen.getByTestId('agent-detail-executability')).toHaveAttribute('data-state', 'not-configured')
    expect(screen.getByTestId('agent-detail-executability-gap-instructions-missing')).toHaveTextContent(
      /Instructions are missing/i,
    )
    expect(
      screen.getByTestId('agent-detail-executability-gap-instructions-missing').querySelector('a'),
    ).toHaveAttribute('href', '/Test/agents/agent-1')
    expect(screen.getByTestId('agent-detail-new-session')).toBeDisabled()
  })

  it('renders executable without synthesizing another verdict', () => {
    state.agent = makeAgent({ executability: { state: 'executable', gaps: [], pendingLaunchNote: null } })
    renderPage()

    expect(screen.getByTestId('agent-detail-executability')).toHaveAttribute('data-state', 'executable')
    expect(screen.queryByTestId('agent-detail-executability-gaps')).not.toBeInTheDocument()
  })

  it('keeps unknown launchable', () => {
    state.agent = makeAgent({
      executability: { state: 'unknown', gaps: [], pendingLaunchNote: 'Awaiting Runner verification.' },
    })
    renderPage()

    expect(screen.getByTestId('agent-detail-executability')).toHaveAttribute('data-state', 'unknown')
    expect(screen.getByTestId('agent-detail-executability-pending-note')).toHaveTextContent(/awaiting runner/i)
    expect(screen.getByTestId('agent-detail-new-session')).not.toBeDisabled()
  })

  it('renders the server availability conclusion and waiting-work reasons', () => {
    state.detailStatus = {
      agentId: 'agent-1',
      agentName: 'Test Agent',
      availability: {
        canStartNow: false,
        waitingReason: 'capacity-full',
        activeRuns: 2,
        maxConcurrentRuns: 2,
        capacity: { usedSlots: 2, totalSlots: 2 },
        observedAt: '2026-07-29T00:00:00.000Z',
      },
      waitingWork: [
        { jobId: 'job-1', status: 'waiting', waitingReason: 'capacity-full', submittedAt: '2026-07-29T00:00:00.000Z' },
        {
          jobId: 'job-2',
          status: 'waiting',
          waitingReason: 'concurrency-limit',
          submittedAt: '2026-07-29T00:00:00.000Z',
        },
        {
          jobId: 'job-3',
          status: 'waiting',
          waitingReason: 'dispatch-pending',
          submittedAt: '2026-07-29T00:00:00.000Z',
        },
      ],
    }
    renderPage()

    const card = screen.getByTestId('agent-detail-availability')
    expect(card).toHaveAttribute('data-state', 'waiting')
    expect(card).toHaveAttribute('data-waiting-reason', 'capacity-full')
    expect(screen.getByTestId('agent-detail-availability-feedback')).toHaveAttribute(
      'data-feedback-kind',
      'back-pressure',
    )
    expect(screen.getByTestId('agent-detail-availability-feedback')).toHaveTextContent(/wait for a runner slot/i)
    expect(screen.getByTestId('agent-detail-waiting-work-job-1')).toHaveAttribute(
      'data-waiting-reason',
      'capacity-full',
    )
    expect(screen.getByTestId('agent-detail-waiting-work-job-2')).toHaveAttribute(
      'data-waiting-reason',
      'concurrency-limit',
    )
    expect(screen.getByTestId('agent-detail-waiting-work-job-3')).toHaveTextContent('Waiting for dispatch')
  })

  it('does not derive a capacity verdict from raw runner slots', () => {
    state.detailStatus = {
      agentId: 'agent-1',
      agentName: 'Test Agent',
      availability: {
        canStartNow: true,
        waitingReason: null,
        activeRuns: 1,
        maxConcurrentRuns: 2,
        capacity: { usedSlots: 2, totalSlots: 2 },
        observedAt: '2026-07-29T00:00:00.000Z',
      },
      waitingWork: [],
    }
    renderPage()

    const card = screen.getByTestId('agent-detail-availability')
    expect(card).toHaveAttribute('data-state', 'ready')
    expect(card.textContent ?? '').not.toMatch(/Runner at capacity/i)
  })

  it('names Runner offline as Availability and gives the connection next step', () => {
    state.detailStatus = {
      agentId: 'agent-1',
      agentName: 'Test Agent',
      availability: {
        canStartNow: false,
        waitingReason: 'no-online-runner',
        activeRuns: 0,
        maxConcurrentRuns: null,
        capacity: { usedSlots: 0, totalSlots: 0 },
        observedAt: '2026-07-29T00:00:00.000Z',
      },
      waitingWork: [],
    }
    renderPage()

    expect(screen.getByTestId('agent-detail-executability-state')).toHaveTextContent('unknown')
    const feedback = screen.getByTestId('agent-detail-availability-feedback')
    expect(feedback).toHaveAttribute('data-feedback-kind', 'runner-offline')
    expect(feedback).toHaveTextContent(/connect a runner/i)
  })
})
