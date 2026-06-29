// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { IssueHealth, IssueStatus, type Issue } from '../../../entities/issue'
import { type AgentCostMetricDto, type AgentStatus } from '../../../entities/agent'
import { ProjectProvider } from '../../../entities/project'
import { FactoryStatusHeadline } from './FactoryStatusHeadline'

const mocks = vi.hoisted(() => ({
  issues: undefined as Issue[] | undefined,
  agentStatus: undefined as AgentStatus | undefined,
  costRollup: undefined as { todayCost: AgentCostMetricDto } | undefined,
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
    useCostRollup: () => ({ data: mocks.costRollup, isLoading: false }),
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

function makeTodayCost(overrides: Partial<AgentCostMetricDto> = {}): AgentCostMetricDto {
  return {
    amount: 1.25,
    currency: 'USD',
    sampleCount: 1,
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
  mocks.costRollup = undefined
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
    expect(screen.getByTestId('factory-status-today-cost')).toBeInTheDocument()
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
      makeIssue({ id: 'ship-1', status: IssueStatus.Done, health: IssueHealth.Done, completedAt: todayIso, updatedAt: todayIso }),
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

describe('FactoryStatusHeadline today-cost', () => {
  it('renders an em-dash placeholder when the rollup is missing', () => {
    mocks.costRollup = undefined
    renderHeadline()

    const field = screen.getByTestId('factory-status-today-cost')
    expect(field).toHaveTextContent('—')
    expect(field).not.toHaveTextContent('$')
  })

  it('renders an em-dash placeholder when todayCost has zero samples (empty / no sessions with usage today)', () => {
    mocks.costRollup = {
      todayCost: makeTodayCost({ amount: null, currency: null, sampleCount: 0 }),
    }
    renderHeadline()

    const field = screen.getByTestId('factory-status-today-cost')
    expect(field).toHaveTextContent('—')
    expect(field).not.toHaveTextContent('$')
    expect(field).not.toHaveTextContent('0')
  })

  it('renders the formatted numeric value when todayCost has a non-empty sample', () => {
    mocks.costRollup = {
      todayCost: makeTodayCost({ amount: 1.25, currency: 'USD', sampleCount: 3 }),
    }
    renderHeadline()

    const field = screen.getByTestId('factory-status-today-cost')
    expect(field).toHaveTextContent('$1.25')
  })

  it('renders a real $0.00 when todayCost is a genuine zero (sampleCount > 0, amount === 0)', () => {
    mocks.costRollup = {
      todayCost: makeTodayCost({ amount: 0, currency: 'USD', sampleCount: 2 }),
    }
    renderHeadline()

    const field = screen.getByTestId('factory-status-today-cost')
    expect(field).toHaveTextContent('$0.00')
  })

  it('prefers the injected todayCost prop over the rollup query', () => {
    mocks.costRollup = {
      todayCost: makeTodayCost({ amount: 9.99, currency: 'USD', sampleCount: 5 }),
    }

    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[demoProject]}>
          <FactoryStatusHeadline
            todayCost={makeTodayCost({ amount: 0.5, currency: 'EUR', sampleCount: 1 })}
          />
        </ProjectProvider>
      </QueryClientProvider>,
    )

    const field = screen.getByTestId('factory-status-today-cost')
    expect(field).toHaveTextContent('€0.50')
  })

  it('keeps the runner/in-flight/awaiting-approval/shipped-today fields unchanged when todayCost is populated', () => {
    mocks.costRollup = {
      todayCost: makeTodayCost({ amount: 1.25, currency: 'USD', sampleCount: 3 }),
    }
    mocks.issues = [
      makeIssue({ id: 'run-1', status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ id: 'approve-1', approvalState: { status: 'awaiting', requestedAt: todayIso } }),
      makeIssue({ id: 'ship-1', status: IssueStatus.Done, health: IssueHealth.Done, completedAt: todayIso, updatedAt: todayIso }),
    ]

    renderHeadline()

    expect(screen.getByTestId('factory-status-runner')).toHaveTextContent('Online')
    expect(screen.getByTestId('factory-status-in-flight')).toHaveTextContent('1')
    expect(screen.getByTestId('factory-status-awaiting-approval')).toHaveTextContent('1')
    expect(screen.getByTestId('factory-status-shipped-today')).toHaveTextContent('1')
    expect(screen.getByTestId('factory-status-today-cost')).toHaveTextContent('$1.25')
  })

  it('does not render the legacy factory-cost-reserved testid', () => {
    mocks.costRollup = {
      todayCost: makeTodayCost({ amount: 1.25, currency: 'USD', sampleCount: 3 }),
    }
    renderHeadline()

    expect(screen.queryByTestId('factory-cost-reserved')).not.toBeInTheDocument()
  })
})
