import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { ProjectProvider } from '../../../entities/project'
import type { AgentAvailabilitySummaryEntry, AgentInfo } from '../../../entities/agent'
import { server, useMswServer } from '../../../../tests/support/msw'
import { AgentListPage, type AgentListPageComponents } from './AgentListPage'

const AGENTS_PATH = '*/api/projects/:projectId/agents'
const AVAILABILITY_PATH = '*/api/projects/:projectId/agents/availability'
const STATUS_PATH = '*/api/projects/:projectId/agent/status'

function mockAgents(agents: AgentInfo[]) {
  server.use(http.get(AGENTS_PATH, () => HttpResponse.json({ success: true, data: agents })))
}
function mockAgentsPending() {
  server.use(http.get(AGENTS_PATH, () => new Promise(() => {})))
}
function mockAvailability(entries: AgentAvailabilitySummaryEntry[]) {
  server.use(http.get(AVAILABILITY_PATH, () => HttpResponse.json({ success: true, data: entries })))
}

useMswServer(
  http.get(AGENTS_PATH, () => HttpResponse.json({ success: true, data: [] })),
  http.get(AVAILABILITY_PATH, () => HttpResponse.json({ success: true, data: [] })),
  http.get(STATUS_PATH, () =>
    HttpResponse.json({ success: true, data: { running: false, capacity: { active: 0, max: 8 } } }),
  ),
)

const components: AgentListPageComponents = {
  AgentProfileEditor: ({ agent, open }) =>
    open ? <div data-testid="agent-profile-editor" data-mode={agent === null ? 'create' : 'edit'} /> : null,
}

function LocationProbe() {
  const location = useLocation()
  return <div data-testid="current-path">{location.pathname}</div>
}

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

function renderPage() {
  const queryClient = createQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
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
        <MemoryRouter initialEntries={['/agents']}>
          <AgentListPage components={components} />
          <LocationProbe />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function makeAgent(overrides: Partial<AgentInfo> = {}): AgentInfo {
  return {
    id: 'agent-1',
    projectId: 'proj-1',
    name: 'Test Agent',
    purpose: 'A test purpose',
    description: 'A test agent',
    instructions: 'Do stuff',
    agentConfig: null,
    skills: [],
    permissions: [],
    maxConcurrentRuns: null,
    status: 'active',
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  }
}

function makeAvailability(overrides: Partial<AgentAvailabilitySummaryEntry> = {}): AgentAvailabilitySummaryEntry {
  return {
    agentId: 'agent-1',
    canStartNow: true,
    waitingReason: null,
    activeRuns: 0,
    maxConcurrentRuns: null,
    capacity: { usedSlots: 0, totalSlots: 4 },
    queuedCount: 0,
    ...overrides,
  }
}

describe('AgentListPage', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  describe('list rendering', () => {
    it('shows loading state while agents are loading', () => {
      mockAgentsPending()
      renderPage()
      expect(screen.getByText(/loading agents/i)).toBeInTheDocument()
    })

    it('renders empty state when no profiles exist', async () => {
      renderPage()
      expect(await screen.findByTestId('agents-empty-state')).toBeInTheDocument()
      expect(screen.getByText(/no agents defined/i)).toBeInTheDocument()
      expect(screen.getByTestId('agents-empty-create')).toBeInTheDocument()
    })

    it('renders active agents in the list', async () => {
      mockAgents([makeAgent({ name: 'Alpha', id: 'a1' })])
      renderPage()
      expect(await screen.findByTestId('agent-row-a1')).toBeInTheDocument()
      expect(screen.getByText('Alpha')).toBeInTheDocument()
    })

    it('renders agent type, model, and variant for each row', async () => {
      mockAgents([
        makeAgent({
          name: 'Beta',
          id: 'b1',
          agentConfig: { model: 'gpt-4', variant: 'high' },
        }),
      ])
      renderPage()
      expect(await screen.findByText('gpt-4')).toBeInTheDocument()
      expect(screen.getByText('high')).toBeInTheDocument()
    })

    it('renders purpose and the server Executability state distinctly', async () => {
      mockAgents([
        makeAgent({
          id: 'ready',
          name: 'Ready Agent',
          purpose: 'Reviews pull requests',
          executability: { state: 'executable', gaps: [], pendingLaunchNote: null },
        }),
        makeAgent({
          id: 'setup',
          name: 'Setup Agent',
          purpose: 'Needs configuration',
          executability: { state: 'not-configured', gaps: [], pendingLaunchNote: null },
        }),
        makeAgent({ id: 'unknown', name: 'Unknown Agent', purpose: null, executability: null }),
      ])
      renderPage()

      const readyRow = await screen.findByTestId('agent-row-ready')
      expect(within(readyRow).getByTestId('agent-purpose-ready')).toHaveTextContent('Reviews pull requests')
      expect(within(readyRow).getByTestId('agent-executability-ready')).toHaveTextContent('Executability: executable')
      expect(within(screen.getByTestId('agent-row-setup')).getByTestId('agent-executability-setup')).toHaveTextContent(
        'Executability: not-configured',
      )
      expect(
        within(screen.getByTestId('agent-row-unknown')).getByTestId('agent-executability-unknown'),
      ).toHaveTextContent('Executability: unknown')
      expect(within(screen.getByTestId('agent-row-unknown')).getByTestId('agent-purpose-unknown')).toHaveTextContent(
        'No purpose set',
      )
    })

    it('renders server Availability and active/queued workload without changing executability', async () => {
      mockAgents([
        makeAgent({
          id: 'offline',
          name: 'Offline Ready',
          executability: { state: 'executable', gaps: [], pendingLaunchNote: null },
        }),
      ])
      mockAvailability([
        makeAvailability({
          agentId: 'offline',
          canStartNow: false,
          waitingReason: 'no-online-runner',
          activeRuns: 2,
          queuedCount: 3,
        }),
      ])
      renderPage()

      const row = await screen.findByTestId('agent-row-offline')
      expect(within(row).getByTestId('agent-executability-offline')).toHaveTextContent('Executability: executable')
      expect(within(row).getByTestId('agent-availability-offline')).toHaveTextContent('Availability: Runner offline')
      expect(within(row).getByTestId('agent-availability-guidance-offline')).toHaveAttribute(
        'data-feedback-kind',
        'runner-offline',
      )
      expect(within(row).getByTestId('agent-availability-guidance-offline')).toHaveTextContent(/connect a runner/i)
      expect(within(row).getByTestId('agent-workload-offline')).toHaveTextContent('Active: 2, Queued: 3')
      expect(within(row).getByTestId('agent-executability-offline')).not.toHaveTextContent('not-configured')
    })

    it.each([
      ['capacity-full', 'Wait for a runner slot to free up'],
      ['concurrency-limit', 'Wait for an active run to finish'],
      ['dispatch-pending', 'Wait for dispatch to complete'],
    ])('gives an actionable next step for %s Availability', async (waitingReason, nextAction) => {
      mockAgents([
        makeAgent({ id: 'waiting', executability: { state: 'executable', gaps: [], pendingLaunchNote: null } }),
      ])
      mockAvailability([
        makeAvailability({
          agentId: 'waiting',
          canStartNow: false,
          waitingReason,
          activeRuns: 1,
          queuedCount: 1,
        }),
      ])
      renderPage()

      const row = await screen.findByTestId('agent-row-waiting')
      const guidance = within(row).getByTestId('agent-availability-guidance-waiting')
      expect(guidance).toHaveAttribute('data-feedback-kind', 'back-pressure')
      expect(guidance).toHaveTextContent(nextAction)
    })

    it('uses one list Availability request for multiple Agents', async () => {
      let availabilityRequests = 0
      server.use(
        http.get(AVAILABILITY_PATH, () => {
          availabilityRequests += 1
          return HttpResponse.json({
            success: true,
            data: [
              makeAvailability({ agentId: 'a1' }),
              makeAvailability({ agentId: 'a2', activeRuns: 1, queuedCount: 2 }),
            ],
          })
        }),
      )
      mockAgents([makeAgent({ id: 'a1' }), makeAgent({ id: 'a2' })])
      renderPage()

      await screen.findByText('Active: 1, Queued: 2')
      expect(availabilityRequests).toBe(1)
      expect(screen.queryByTestId('agent-availability-a1')).toHaveTextContent('Can start now')
      expect(screen.queryByTestId('agent-availability-a2')).toHaveTextContent('Can start now')
    })

    it('shows loading Availability while the summary is unresolved', async () => {
      mockAgents([
        makeAgent({ id: 'pending', executability: { state: 'executable', gaps: [], pendingLaunchNote: null } }),
      ])
      server.use(http.get(AVAILABILITY_PATH, () => new Promise(() => {})))
      renderPage()

      const row = await screen.findByTestId('agent-row-pending')
      expect(within(row).getByTestId('agent-availability-pending')).toHaveTextContent('Availability: Loading')
      expect(within(row).getByTestId('agent-executability-pending')).toHaveTextContent('Executability: executable')
      expect(within(row).getByTestId('agent-executability-pending')).not.toHaveTextContent('not-configured')
    })

    it('distinguishes archived agents with opacity and badge', async () => {
      mockAgents([
        makeAgent({ name: 'Active One', id: 'a1', status: 'active' }),
        makeAgent({ name: 'Archived One', id: 'a2', status: 'archived' }),
      ])
      renderPage()
      const archivedRow = await screen.findByTestId('agent-row-a2')
      expect(archivedRow).toBeInTheDocument()
      expect(archivedRow.getAttribute('data-status')).toBe('archived')
      const archivedLabels = screen.getAllByText('Archived')
      expect(archivedLabels.length).toBeGreaterThanOrEqual(1)
    })

    it('displays availability status (Active / Archived)', async () => {
      mockAgents([
        makeAgent({ id: 'a1', name: 'Active A', status: 'active' }),
        makeAgent({ id: 'a2', name: 'Archived B', status: 'archived' }),
      ])
      renderPage()
      await screen.findByTestId('agent-row-a2')
      const activeStatuses = screen.getAllByText('Active')
      const archivedStatuses = screen.getAllByText('Archived')
      expect(activeStatuses.length).toBeGreaterThanOrEqual(1)
      expect(archivedStatuses.length).toBeGreaterThanOrEqual(1)
    })
  })

  describe('create entry points', () => {
    it('does not render the editor before any entry point is clicked', async () => {
      mockAgents([makeAgent({ id: 'a1', name: 'Alpha' })])
      renderPage()
      await screen.findByTestId('agent-row-a1')
      expect(screen.queryByTestId('agent-profile-editor')).not.toBeInTheDocument()
    })

    it('opens the profile editor in create mode when the header "New Agent" button is clicked (no route change)', async () => {
      mockAgents([makeAgent({ id: 'a1', name: 'Alpha' })])
      renderPage()
      await screen.findByTestId('agent-row-a1')
      fireEvent.click(screen.getByTestId('agent-list-create'))
      expect(screen.getByTestId('agent-profile-editor')).toHaveAttribute('data-mode', 'create')
      expect(screen.getByTestId('current-path')).toHaveTextContent('/agents')
    })

    it('opens the profile editor in create mode when the empty-state "Create Agent" button is clicked (no route change)', async () => {
      renderPage()
      await screen.findByTestId('agents-empty-state')
      fireEvent.click(screen.getByTestId('agents-empty-create'))
      expect(screen.getByTestId('agent-profile-editor')).toHaveAttribute('data-mode', 'create')
      expect(screen.getByTestId('current-path')).toHaveTextContent('/agents')
    })
  })

  describe('Archived section (agent-archive spec)', () => {
    it('lists archived agents under an "Archived (n)" section whose count matches', async () => {
      mockAgents([
        makeAgent({ id: 'a-active', name: 'Active One', status: 'active' }),
        makeAgent({ id: 'a-archived', name: 'Archived One', status: 'archived' }),
        makeAgent({ id: 'b-archived', name: 'Archived Two', status: 'archived' }),
      ])
      renderPage()
      const section = await screen.findByTestId('archived-section')
      expect(section).toBeInTheDocument()
      expect(section).toHaveTextContent('Archived (2)')
      expect(within(section).getByTestId('agent-row-a-archived')).toBeInTheDocument()
      expect(within(section).getByTestId('agent-row-b-archived')).toBeInTheDocument()
    })

    it('renders archived rows with reduced opacity (visually distinct) and an Archived badge', async () => {
      mockAgents([
        makeAgent({ id: 'a-active', name: 'Active One', status: 'active' }),
        makeAgent({ id: 'a-archived', name: 'Archived One', status: 'archived' }),
      ])
      renderPage()
      const archivedRow = await screen.findByTestId('agent-row-a-archived')
      const activeRow = screen.getByTestId('agent-row-a-active')
      expect(archivedRow.className).toMatch(/opacity-60/)
      expect(activeRow.className).not.toMatch(/opacity-60/)
      const archivedLabels = within(archivedRow).getAllByText('Archived')
      expect(archivedLabels.length).toBeGreaterThanOrEqual(1)
    })

    it('archived rows navigate into the detail page like active rows', async () => {
      mockAgents([
        makeAgent({ id: 'a-active', name: 'Active One', status: 'active' }),
        makeAgent({ id: 'a-archived', name: 'Archived One', status: 'archived' }),
      ])
      renderPage()
      await screen.findByTestId('agent-row-a-archived')
      fireEvent.click(screen.getByTestId('agent-row-a-archived'))
      expect(screen.getByTestId('current-path')).toHaveTextContent('/agents/a-archived')
    })

    it('omits the Archived section when no archived agents exist', async () => {
      mockAgents([
        makeAgent({ id: 'a1', name: 'Active One', status: 'active' }),
        makeAgent({ id: 'a2', name: 'Active Two', status: 'active' }),
      ])
      renderPage()
      await screen.findByTestId('agent-row-a1')
      expect(screen.queryByTestId('archived-section')).not.toBeInTheDocument()
    })
  })
})
