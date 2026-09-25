import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'
import { IssueHealth, IssueStatus, type Issue } from '../../../entities/issue'
import { type AgentCostMetricDto, type AgentStatus } from '../../../entities/agent'
import { ProjectProvider } from '../../../entities/project'
import { deriveRunnerSummary, type RunnerStatusEntry, type RunnerStatusSummary } from '../../../entities/runner'
import { server, useMswServer } from '../../../../tests/support/msw'
import { FactoryStatusHeadline } from './FactoryStatusHeadline'

const ISSUES_PATH = `*/api/projects/:projectId/issues`
const STATUS_PATH = `*/api/projects/:projectId/agent/status`
const COST_PATH = `*/api/projects/:projectId/agent/cost`

function mockIssues(issues: Issue[]) {
  server.use(http.get(ISSUES_PATH, () => HttpResponse.json({ success: true, data: issues })))
}
function mockAgentStatus(status: AgentStatus) {
  server.use(http.get(STATUS_PATH, () => HttpResponse.json({ success: true, data: status })))
}
function mockCostRollup(costRollup: { todayCost: AgentCostMetricDto }) {
  server.use(http.get(COST_PATH, () => HttpResponse.json({ success: true, data: costRollup })))
}

const now = new Date('2026-06-26T12:00:00.000Z')
const todayIso = now.toISOString()

useMswServer(
  http.get(ISSUES_PATH, () => HttpResponse.json({ success: true, data: [] })),
  http.get(STATUS_PATH, () => HttpResponse.json({ success: true, data: makeAgentStatus({ runnerAvailable: true }) })),
  http.get(COST_PATH, () =>
    HttpResponse.json({
      success: true,
      data: {
        totalCost: { amount: null, currency: null, sampleCount: 0 },
        todayCost: { amount: null, currency: null, sampleCount: 0 },
        doneIssuesCount: 0,
        costPerShip: { amount: null, currency: null, sampleCount: 0 },
      },
    }),
  ),
)

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
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
    issueNumber: null,
    activeAgents: [],
    capacity: { active: 0, max: 8 },
    runnerAvailable: true,
    runnerMessage: null,
    ...overrides,
  }
}

function makeRunner(overrides: Partial<RunnerStatusEntry> = {}): RunnerStatusEntry {
  return {
    identity: {
      id: 'runner-1',
      hostname: 'host-1',
      kind: 'external',
      component: null,
      sourceRevision: null,
      releaseId: null,
      generation: null,
    },
    presence: { state: 'online', lastObservedAt: null },
    control: { state: 'connected', generation: null },
    admission: { state: 'ready', reasonCodes: [] },
    capabilities: [],
    runtimes: [],
    capacity: { used: 0, total: 8 },
    activeWorks: [],
    drain: null,
    nextActions: [],
    ...overrides,
  }
}

function makeRunnerSummary(row = makeRunner()): RunnerStatusSummary {
  return deriveRunnerSummary([row])
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

function renderHeadline(runnerSummary = makeRunnerSummary(), issues?: Issue[]) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <ProjectProvider initialProjectId="proj-1" initialProjects={[demoProject]}>
          <FactoryStatusHeadline
            issues={issues}
            runnerSummary={runnerSummary}
            runnerSummaryHook={() => runnerSummary}
          />
        </ProjectProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  vi.setSystemTime(new Date('2026-06-26T12:00:00.000Z'))
})

afterEach(() => {
  vi.useRealTimers()
  cleanup()
})

describe('FactoryStatusHeadline rendering', () => {
  it('renders the headline with all fields', async () => {
    renderHeadline()

    expect(await screen.findByText('Capacity available')).toBeInTheDocument()
    expect(screen.getByTestId('factory-status-runner')).toHaveTextContent('Capacity available')
    expect(screen.getByTestId('factory-status-in-flight')).toHaveTextContent('0')
    expect(screen.getByTestId('factory-status-awaiting-approval')).toHaveTextContent('0')
    expect(screen.getByTestId('factory-status-shipped-today')).toHaveTextContent('0')
    expect(screen.getByTestId('factory-status-today-cost')).toBeInTheDocument()
  })

  it('renders zero counts instead of hiding when there is no activity', async () => {
    mockIssues([])
    renderHeadline()

    expect(await screen.findByText('Capacity available')).toBeInTheDocument()
    expect(screen.getByTestId('factory-status-in-flight')).toHaveTextContent('0')
    expect(screen.getByTestId('factory-status-awaiting-approval')).toHaveTextContent('0')
    expect(screen.getByTestId('factory-status-shipped-today')).toHaveTextContent('0')
  })

  it('reflects live field values from injected data', async () => {
    mockIssues([
      makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ approvalState: { status: 'awaiting', requestedAt: todayIso } }),
      makeIssue({ status: IssueStatus.Done, health: IssueHealth.Done, completedAt: todayIso, updatedAt: todayIso }),
    ])
    mockAgentStatus(makeAgentStatus({ runnerAvailable: true }))

    renderHeadline(makeRunnerSummary(), [
      makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ approvalState: { status: 'awaiting', requestedAt: todayIso } }),
      makeIssue({ status: IssueStatus.Done, health: IssueHealth.Done, completedAt: todayIso, updatedAt: todayIso }),
    ])

    expect(await screen.findByText('Capacity available')).toBeInTheDocument()
    expect(screen.getByTestId('factory-status-in-flight')).toHaveTextContent('2')
    expect(screen.getByTestId('factory-status-awaiting-approval')).toHaveTextContent('1')
    expect(screen.getByTestId('factory-status-shipped-today')).toHaveTextContent('1')
  })

  it('shows runner as unavailable when runnerAvailable is not true', async () => {
    cleanup()
    renderHeadline(
      makeRunnerSummary(
        makeRunner({
          admission: { state: 'blocked', reasonCodes: ['control-disconnected'] },
          control: { state: 'disconnected', generation: null },
        }),
      ),
    )

    expect(await screen.findByText('Admission blocked')).toBeInTheDocument()
    expect(screen.getByTestId('factory-status-runner-facts')).toHaveTextContent('control disconnected')
  })

  it('uses injected props over query data when provided', async () => {
    mockIssues([makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Active })])
    mockAgentStatus(makeAgentStatus({ runnerAvailable: false }))

    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <ProjectProvider initialProjectId="proj-1" initialProjects={[demoProject]}>
            <FactoryStatusHeadline
              issues={[]}
              runnerSummary={makeRunnerSummary()}
              runnerSummaryHook={() => makeRunnerSummary()}
            />
          </ProjectProvider>
        </MemoryRouter>
      </QueryClientProvider>,
    )

    expect(screen.getByTestId('factory-status-runner')).toHaveTextContent('Capacity available')
    expect(screen.getByTestId('factory-status-in-flight')).toHaveTextContent('0')
  })

  it('keeps an online ready pool available while offline capacity is excluded', () => {
    const availableRunner = makeRunner({ capacity: { used: 0, total: 8 } })
    const offlineRunners = Array.from({ length: 6 }, (_, index) =>
      makeRunner({
        identity: { ...makeRunner().identity, id: `offline-${index}` },
        presence: { state: 'offline', lastObservedAt: null },
        admission: { state: 'blocked', reasonCodes: ['presence-offline'] },
        capacity: { used: null, total: index === 0 ? 4 : 1 },
      }),
    )
    const summary = deriveRunnerSummary({
      observedAt: '2026-09-25T12:00:00Z',
      inventory: { state: 'ready', nextActions: [] },
      runners: [availableRunner, ...offlineRunners],
    })

    renderHeadline(summary)

    expect(screen.getByTestId('factory-status-runner')).toHaveTextContent('Capacity available')
    expect(screen.getByTestId('factory-status-runner-capacity')).toHaveTextContent('0/8 occupied')
    expect(screen.getByTestId('factory-status-runner-facts')).toHaveTextContent(
      '6 offline / 9 configured slots, occupancy unknown',
    )
    expect(screen.getByTestId('factory-status-runner-facts')).not.toHaveTextContent('unknown/17')
    expect(screen.getByText('Observed 2026-09-25T12:00:00Z')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Inspect Runner reasons' })).toHaveAttribute('href', '/runners')
  })
})

describe('FactoryStatusHeadline today-cost', () => {
  it('renders an em-dash placeholder when the rollup is missing', () => {
    renderHeadline()

    const field = screen.getByTestId('factory-status-today-cost')
    expect(field).toHaveTextContent('—')
    expect(field).not.toHaveTextContent('$')
  })

  it('renders an em-dash placeholder when todayCost has zero samples (empty / no sessions with usage today)', () => {
    mockCostRollup({
      todayCost: makeTodayCost({ amount: null, currency: null, sampleCount: 0 }),
    })
    renderHeadline()

    const field = screen.getByTestId('factory-status-today-cost')
    expect(field).toHaveTextContent('—')
    expect(field).not.toHaveTextContent('$')
    expect(field).not.toHaveTextContent('0')
  })

  it('renders the formatted numeric value when todayCost has a non-empty sample', async () => {
    mockCostRollup({
      todayCost: makeTodayCost({ amount: 1.25, currency: 'USD', sampleCount: 3 }),
    })
    renderHeadline()

    expect(await screen.findByText('$1.25')).toBeInTheDocument()
  })

  it('renders a real $0.00 when todayCost is a genuine zero (sampleCount > 0, amount === 0)', async () => {
    mockCostRollup({
      todayCost: makeTodayCost({ amount: 0, currency: 'USD', sampleCount: 2 }),
    })
    renderHeadline()

    expect(await screen.findByText('$0.00')).toBeInTheDocument()
  })

  it('prefers the injected todayCost prop over the rollup query', () => {
    mockCostRollup({
      todayCost: makeTodayCost({ amount: 9.99, currency: 'USD', sampleCount: 5 }),
    })

    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <ProjectProvider initialProjectId="proj-1" initialProjects={[demoProject]}>
            <FactoryStatusHeadline todayCost={makeTodayCost({ amount: 0.5, currency: 'EUR', sampleCount: 1 })} />
          </ProjectProvider>
        </MemoryRouter>
      </QueryClientProvider>,
    )

    const field = screen.getByTestId('factory-status-today-cost')
    expect(field).toHaveTextContent('€0.50')
  })

  it('keeps the runner/in-flight/awaiting-approval/shipped-today fields unchanged when todayCost is populated', async () => {
    mockCostRollup({
      todayCost: makeTodayCost({ amount: 1.25, currency: 'USD', sampleCount: 3 }),
    })
    mockIssues([
      makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ approvalState: { status: 'awaiting', requestedAt: todayIso } }),
      makeIssue({ status: IssueStatus.Done, health: IssueHealth.Done, completedAt: todayIso, updatedAt: todayIso }),
    ])

    renderHeadline()

    expect(await screen.findByText('$1.25')).toBeInTheDocument()
    expect(screen.getByTestId('factory-status-runner')).toHaveTextContent('Capacity available')
    expect(screen.getByTestId('factory-status-in-flight')).toHaveTextContent('1')
    expect(screen.getByTestId('factory-status-awaiting-approval')).toHaveTextContent('1')
    expect(screen.getByTestId('factory-status-shipped-today')).toHaveTextContent('1')
  })

  it('does not render the legacy factory-cost-reserved testid', async () => {
    mockCostRollup({
      todayCost: makeTodayCost({ amount: 1.25, currency: 'USD', sampleCount: 3 }),
    })
    renderHeadline()

    expect(screen.queryByTestId('factory-cost-reserved')).not.toBeInTheDocument()
  })
})
