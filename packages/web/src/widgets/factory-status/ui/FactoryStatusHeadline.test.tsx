// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { IssueHealth, IssueStatus, type Issue } from '../../../entities/issue'
import { type AgentStatus } from '../../../entities/agent'
import { ProjectProvider } from '../../../entities/project'
import { FactoryStatusHeadline } from './FactoryStatusHeadline'

const mocks = vi.hoisted(() => ({
  issues: undefined as Issue[] | undefined,
  agentStatus: undefined as AgentStatus | undefined,
}))

const now = new Date('2026-06-26T12:00:00.000Z')
const todayIso = now.toISOString()

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    useIssues: () => ({ data: mocks.issues, isLoading: false }),
  }
})

vi.mock('../../../entities/agent', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/agent')>()
  return {
    ...actual,
    useAgentStatus: () => ({ data: mocks.agentStatus, isLoading: false }),
  }
})

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: `issue-${Math.random().toString(36).slice(2, 8)}`,
    number: 100,
    title: 'Default issue title',
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-06-18T00:00:00.000Z',
    updatedAt: '2026-06-18T00:00:00.000Z',
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

function makeAgentStatus(overrides: Partial<AgentStatus> = {}): AgentStatus {
  return {
    running: false,
    issueId: null,
    issueNumber: null,
    activeAgents: [],
    capacity: { active: 0, max: 8 },
    runnerAvailable: true,
    runnerMessage: null,
    ...overrides,
  }
}

const demoProject = {
  id: 'proj-1',
  name: 'demo',
  createdAt: '',
  updatedAt: '',
  repositories: [],
}

function renderHeadline() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[demoProject]}>
        <FactoryStatusHeadline />
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  vi.setSystemTime(new Date('2026-06-26T12:00:00.000Z'))
  mocks.issues = []
  mocks.agentStatus = makeAgentStatus({ runnerAvailable: true })
})

afterEach(() => {
  vi.useRealTimers()
  cleanup()
})

describe('FactoryStatusHeadline rendering', () => {
  it('renders the headline with all fields', () => {
    renderHeadline()

    expect(screen.getByTestId('factory-status-headline')).toBeInTheDocument()
    expect(screen.getByTestId('factory-status-runner')).toHaveTextContent('Online')
    expect(screen.getByTestId('factory-status-in-flight')).toHaveTextContent('0')
    expect(screen.getByTestId('factory-status-awaiting-approval')).toHaveTextContent('0')
    expect(screen.getByTestId('factory-status-shipped-today')).toHaveTextContent('0')
    expect(screen.getByTestId('factory-cost-reserved')).toBeInTheDocument()
  })

  it('renders zero counts instead of hiding when there is no activity', () => {
    mocks.issues = []
    renderHeadline()

    expect(screen.getByTestId('factory-status-in-flight')).toHaveTextContent('0')
    expect(screen.getByTestId('factory-status-awaiting-approval')).toHaveTextContent('0')
    expect(screen.getByTestId('factory-status-shipped-today')).toHaveTextContent('0')
  })

  it('reflects live field values from injected data', () => {
    mocks.issues = [
      makeIssue({ id: 'run-1', status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ id: 'run-2', status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ id: 'approve-1', approvalState: { status: 'awaiting', requestedAt: todayIso } }),
      makeIssue({ id: 'ship-1', status: IssueStatus.Done, health: IssueHealth.Done, updatedAt: todayIso }),
    ]
    mocks.agentStatus = makeAgentStatus({ runnerAvailable: true })

    renderHeadline()

    expect(screen.getByTestId('factory-status-runner')).toHaveTextContent('Online')
    expect(screen.getByTestId('factory-status-in-flight')).toHaveTextContent('2')
    expect(screen.getByTestId('factory-status-awaiting-approval')).toHaveTextContent('1')
    expect(screen.getByTestId('factory-status-shipped-today')).toHaveTextContent('1')
  })

  it('shows runner as unavailable when runnerAvailable is not true', () => {
    mocks.agentStatus = makeAgentStatus({ runnerAvailable: false })
    renderHeadline()

    expect(screen.getByTestId('factory-status-runner')).toHaveTextContent('Unavailable')

    mocks.agentStatus = makeAgentStatus({ runnerAvailable: undefined })
    cleanup()
    renderHeadline()

    expect(screen.getByTestId('factory-status-runner')).toHaveTextContent('Unavailable')
  })

  it('renders today-cost placeholder without a numeric zero', () => {
    renderHeadline()

    const placeholder = screen.getByTestId('factory-cost-reserved')
    expect(placeholder).toHaveTextContent('—')
    expect(placeholder).not.toHaveTextContent('0')
  })

  it('uses injected props over query data when provided', () => {
    mocks.issues = [makeIssue({ id: 'ignored', status: IssueStatus.InProgress, health: IssueHealth.Active })]
    mocks.agentStatus = makeAgentStatus({ runnerAvailable: false })

    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[demoProject]}>
          <FactoryStatusHeadline
            issues={[]}
            agentStatus={makeAgentStatus({ runnerAvailable: true })}
          />
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect(screen.getByTestId('factory-status-runner')).toHaveTextContent('Online')
    expect(screen.getByTestId('factory-status-in-flight')).toHaveTextContent('0')
  })
})
