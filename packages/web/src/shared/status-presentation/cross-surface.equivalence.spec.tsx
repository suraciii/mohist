// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { IssueHealth, IssueStatus } from '@/entities/issue'
import { STAGE_FAMILY_RESERVATION } from '@/widgets/kanban-board/model/stage-colors'
import type { Issue } from '@/entities/issue'
import type { AgentStatus } from '@/entities/agent'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  familyFor,
  statusTreatment,
  type SemanticFamily,
} from '@/shared/status-presentation'
import { WorkflowRunStatusPill } from '@/widgets/issue-workflow/ui/WorkflowRunStatusPill'
import { IssueCard } from '@/widgets/kanban-board/ui/IssueCard'
import { RunnerList } from '@/widgets/runner-status/ui/RunnerList'
import { RunnerSummary } from '@/widgets/runner-status/ui/RunnerSummary'
import { ContextHealthIndicator } from '@/widgets/session-health/ui/ContextHealthIndicator'
import { ContextHealthBar } from '@/widgets/session-health/ui/ContextHealthBar'
import { AttentionHero } from '@/widgets/attention-hero/ui/AttentionHero'
import { CompactSessionCard } from '@/widgets/dashboard-pulse/ui/CompactSessionCard'
import { StatusBar } from '@/shared/ui/StatusBar'
import { statusBadge } from '@/entities/issue/lib/status-badge'
import type { RunnerStatusRow, RunnerStatusSummary } from '@/entities/runner/model/types'
import { ProjectProvider } from '@/entities/project'
import type { SessionCard } from '@/widgets/coder-session/model/activity-cards'

afterEach(() => {
  cleanup()
})

/**
 * Cross-surface equivalence spec (design D8).
 *
 * For every covered domain state, two or more widgets can render that
 * state. The spec asserts that `familyFor(kind, state)` returns the same
 * family regardless of which widget renders it — the family is the
 * single source of truth for status meaning.
 *
 * The spec reads `data-family` (a hook added by every widget in this
 * task) so it survives any future class-string refactor while still
 * catching a widget that reintroduces a divergent hue family.
 */

const TEST_PROJECT = {
  id: 'proj-1',
  name: 'demo',
  createdAt: '',
  updatedAt: '',
  repositories: [],
  path: '/tmp/p1',
}

function withRouter(ui: React.ReactNode, options: { withQueryClient?: boolean } = {}) {
  const content = (
    <MemoryRouter initialEntries={['/demo']}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[TEST_PROJECT as never]}>
        {ui}
      </ProjectProvider>
    </MemoryRouter>
  )
  if (options.withQueryClient) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    return <QueryClientProvider client={queryClient}>{content}</QueryClientProvider>
  }
  return content
}

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Demo issue',
    status: 'Backlog' as Issue['status'],
    health: IssueHealth.Active,
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    isDraft: false,
    canStart: true,
    blocker: null,
    approvalState: null,
    ...overrides,
  } as Issue
}

const AGENT_IDLE: AgentStatus = {
  running: false,
  issueId: null,
  issueNumber: null,
  activeAgents: [],
  capacity: { active: 0, max: 8 },
  runnerAvailable: true,
  runnerMessage: null,
}

function makeRunnerRow(overrides: Partial<RunnerStatusRow> = {}): RunnerStatusRow {
  return {
    id: 'runner-1',
    kind: 'external',
    hostname: 'host',
    scope: { type: 'global' },
    status: 'idle',
    capabilities: ['workflow'],
    coderModels: ['openai/gpt-4.5'],
    coderModelCount: 1,
    registeredAt: '2026-01-01T00:00:00.000Z',
    lastHeartbeatAt: '2026-01-01T12:00:00.000Z',
    connectionState: 'connected',
    activeWorks: [],
    ...overrides,
  } as RunnerStatusRow
}

function makeSessionCard(overrides: Partial<SessionCard> = {}): SessionCard {
  return {
    issueId: 'issue-1',
    issueNumber: '1',
    issueTitle: 'Demo session',
    issueStage: 'Build',
    sessionId: 'session-1',
    status: 'active',
    model: 'claude-opus-4-7',
    resolvedModel: null,
    taskDescription: 'demo',
    title: 'demo',
    createdAt: '2026-01-01T00:00:00Z',
    completedAt: null,
    lastActivityAt: '2026-01-01T00:00:30Z',
    activityPreviews: [{ kind: 'text', text: 'preview' }],
    taskProgress: null,
    currentWorkTitle: 'demo',
    failureReason: null,
    failureCategory: null,
    inputTokens: null,
    outputTokens: null,
    totalTokens: null,
    costAmount: null,
    costCurrency: null,
    contextWindowUsed: null,
    contextWindowSize: null,
    contextUsagePercent: null,
    toolCallCount: null,
    toolErrorCount: null,
    healthStatus: null,
    ...overrides,
  } as SessionCard
}

function dataFamily(testId: string): string | null {
  const el = screen.getByTestId(testId)
  return el.getAttribute('data-family')
}

describe('cross-surface status equivalence (design D8)', () => {
  describe('workflow-run: WorkflowRunStatusPill ↔ StatusBar', () => {
    const STATUS_BAR_KEY_TO_STATE = {
      active: 'running',
      waiting: 'awaiting-approval',
      completed: 'completed',
      failed: 'failed',
    } as const

    it.each([
      'completed',
      'failed',
      'awaiting-approval',
      'running',
      'pending',
      'stopped',
      'paused',
      'created',
    ] as const)('workflow-run state "%s" resolves to a single family across the layer', (state) => {
      render(<WorkflowRunStatusPill status={state} />)
      const rendered = dataFamily(`workflow-run-status-${state}`)
      const expected = familyFor('workflow-run', state)
      expect(rendered).toBe(expected)
      // The single source: `statusTreatment` returns the same family
      expect(statusTreatment('workflow-run', state).family).toBe(expected)
    })

    it.each(Object.entries(STATUS_BAR_KEY_TO_STATE))(
      'StatusBar key "%s" (workflow-run "%s") agrees with familyFor and WorkflowRunStatusPill',
      (statusBarKey, workflowState) => {
        render(
          <StatusBar
            active={1}
            waiting={1}
            completed={1}
            failed={1}
            activeSlots={1}
            maxSlots={8}
          />,
        )
        const barKey = statusBarKey as keyof typeof STATUS_BAR_KEY_TO_STATE
        const barBadge = screen.getByTestId(`status-bar-${barKey}`)
        const barFamily = barBadge.getAttribute('data-family')
        const expectedFamily = familyFor('workflow-run', workflowState)

        // Both surfaces must agree with the single source.
        expect(barFamily).toBe(expectedFamily)

        // Cross-check: WorkflowRunStatusPill for the same workflow state
        // produces the same family (no `emerald` vs `green` vs `#22c55e`
        // divergence — both consume `statusTreatment`).
        cleanup()
        render(<WorkflowRunStatusPill status={workflowState} />)
        const pill = dataFamily(`workflow-run-status-${workflowState}`)
        expect(pill).toBe(expectedFamily)
        expect(barFamily).toBe(pill)
      },
    )
  })

  describe('runner: RunnerList ↔ RunnerSummary', () => {
    it.each(['idle', 'busy', 'stale', 'offline'] as const)(
      'runner state "%s" is identical between RunnerList and RunnerSummary',
      (state) => {
        const row = makeRunnerRow({ status: state, connectionState: 'connected' })
        // RunnerList rendering — `runner-row` is the per-runner row that carries the badge
        render(withRouter(<RunnerList rows={[row]} />))
        const listBadge = screen.getByText(
          state === 'idle' ? 'idle'
            : state === 'busy' ? 'busy'
            : state === 'stale' ? 'stale'
            : 'offline',
        ).closest('[data-family]') as HTMLElement | null
        const listFamily = listBadge?.getAttribute('data-family') ?? null
        cleanup()

        const summary: RunnerStatusSummary = {
          rows: [row],
          connectedIdleCount: state === 'idle' ? 1 : 0,
          connectedBusyCount: state === 'busy' ? 1 : 0,
          hasConnectedCapacity: state === 'idle' || state === 'busy',
        }
        render(withRouter(<RunnerSummary summary={summary} />))
        const summaryBadge = document.querySelector('[data-family]') as HTMLElement | null
        const summaryFamily = summaryBadge?.getAttribute('data-family') ?? null

        const expectedFamily = familyFor('runner', state)
        expect(listFamily, `RunnerList family for ${state}`).toBe(expectedFamily)
        expect(summaryFamily, `RunnerSummary family for ${state}`).toBe(expectedFamily)
        expect(listFamily, 'cross-surface family must agree').toBe(summaryFamily)
      },
    )
  })

  describe('issue-health: statusBadge delegate ↔ IssueCard StatusPill', () => {
    it.each([
      [IssueHealth.Active, 'active'],
      [IssueHealth.Blocked, 'blocked'],
      [IssueHealth.Interrupted, 'interrupted'],
      [IssueHealth.Done, 'done'],
      [IssueHealth.Cancelled, 'cancelled'],
      [IssueHealth.Paused, 'paused'],
    ] as const)(
      'issue-health state "%s" matches between statusBadge() delegate and IssueCard',
      (health, _label) => {
        // statusBadge() is a thin delegate — its returned class set must
        // be exactly the shared layer's container for the same family.
        const expectedFamily = familyFor('issue-health', health)
        const expectedContainer = statusTreatment('issue-health', health).container
        expect(statusBadge(health)).toBe(expectedContainer)

        // IssueCard's StatusPill only renders when the indicator is non-null.
        // For health states that map to a pill indicator, render the card
        // and read the pill's data-family.
        const indicatorMatch: Partial<Record<typeof health, boolean>> = {
          [IssueHealth.Blocked]: true,
          [IssueHealth.Interrupted]: false, // Interrupted alone doesn't set an indicator
          [IssueHealth.Done]: false,        // Done doesn't set an indicator
        }
        if (!indicatorMatch[health]) {
          // Verify the layer's family at minimum.
          expect(expectedFamily).toBeTruthy()
          return
        }
        const issue = makeIssue({ health })
        render(withRouter(<IssueCard issue={issue} agentStatus={AGENT_IDLE} />, { withQueryClient: true }))
        const pill = screen.getByTestId('status-pill')
        expect(pill.getAttribute('data-family')).toBe(expectedFamily)
      },
    )
  })

  describe('context-health: ContextHealthIndicator ↔ ContextHealthBar', () => {
    it.each([
      ['green', 'success'],
      ['yellow', 'warning'],
      ['red', 'danger'],
    ] as const)(
      'context-health "%s" resolves to the same family on indicator and bar',
      (status, expected) => {
        const expectedFamily = familyFor('context-health', status) as SemanticFamily
        expect(expectedFamily).toBe(expected)

        // Indicator path
        render(
          <ContextHealthIndicator
            contextWindowUsed={500_000}
            contextWindowSize={1_000_000}
            contextUsagePercent={50}
            healthStatus={status}
          />,
        )
        const indicatorFamily = dataFamily('context-health-indicator')
        cleanup()

        // Bar path
        render(
          <ContextHealthBar
            contextWindowUsed={500_000}
            contextWindowSize={1_000_000}
            contextUsagePercent={50}
            healthStatus={status}
          />,
        )
        const bar = screen.getByTestId('context-health-bar')
        const barFamily = bar.getAttribute('data-family')

        expect(indicatorFamily).toBe(expected)
        expect(barFamily).toBe(expected)
      },
    )
  })

  describe('attention hero all-clear ↔ runner idle family', () => {
    it('renders the success family on AttentionHero all-clear (matches runner idle)', () => {
      // Empty issues + runner available → all-clear state, which the spec
      // maps to the same `success` family as `runner.idle`.
      const heroTree = (
        <AttentionHero issues={[]} agentStatus={{ ...AGENT_IDLE, runnerAvailable: true }} />
      )
      const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
      const tree = (
        <QueryClientProvider client={queryClient}>
          <MemoryRouter initialEntries={['/demo']}>
            <ProjectProvider initialProjectId="proj-1" initialProjects={[TEST_PROJECT as never]}>
              {heroTree}
            </ProjectProvider>
          </MemoryRouter>
        </QueryClientProvider>
      )
      const { rerender } = render(tree)
      const hero = screen.getByTestId('dashboard-zone-attention')
      const heroFamily = hero.getAttribute('data-family')

      const idleFamily = familyFor('runner', 'idle')
      expect(heroFamily).toBe(idleFamily)
      expect(idleFamily).toBe('success')
      // Sanity: same hero agrees with itself across rerenders.
      rerender(tree)
      expect(screen.getByTestId('dashboard-zone-attention').getAttribute('data-family')).toBe('success')
    })
  })

  describe('CompactSessionCard stage palette is dark-aware (categorical, not state)', () => {
    it('does not borrow a semantic-family class — categorical identity, separate palette', () => {
      render(withRouter(<CompactSessionCard card={makeSessionCard({ issueStage: 'Build' })} />))
      const stage = screen.getByTestId('pulse-compact-stage')
      const cls = stage.className
      // Categorical — not a state semantic family — but dark-aware:
      expect(cls).toMatch(/bg-purple-100/)
      expect(cls).toMatch(/dark:/)
    })
  })

  describe('kanban stage colors ↔ shared layer', () => {
    it('InProgress column resolves to the same family as active issue-health and running workflow-stage', () => {
      const kanbanFamily = STAGE_FAMILY_RESERVATION[IssueStatus.InProgress]
      expect(kanbanFamily).toBe('info')
      expect(kanbanFamily).toBe(familyFor('issue-health', 'active'))
      expect(kanbanFamily).toBe(familyFor('workflow-stage', 'running'))
    })

    it('Done column stays in sync with the shared layer', () => {
      expect(STAGE_FAMILY_RESERVATION[IssueStatus.Done]).toBe(familyFor('issue-health', 'done'))
    })

    it('Cancelled column stays in sync with the shared layer', () => {
      expect(STAGE_FAMILY_RESERVATION[IssueStatus.Cancelled]).toBe(familyFor('issue-health', 'cancelled'))
    })
  })
})
