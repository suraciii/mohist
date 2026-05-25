// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi, beforeEach } from 'vitest'
import { act, cleanup, render, screen, fireEvent, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { KanbanBoard } from './KanbanBoard'
import { Stage, IssueStatus } from '../lib/types'
import type { Issue, AgentStatus, ApprovalState } from '../lib/types'
import {
  parseBoardQuery,
  serializeBoardQuery,
  deriveBoardColumns,
  applyBoardFilters,
  type BoardQueryState,
} from '../lib/board-query'
import { groupIssuesByStage } from '../lib/kanban-grouping'

const { LABELS_MOCK } = vi.hoisted(() => ({
  LABELS_MOCK: ['bug', 'feature', 'docs', 'workflow', 'ux', 'webui', 'improvement', 'reliability', 'session', 'agent'],
}))

vi.mock('../hooks/useQueries', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../hooks/useQueries')>()
  return {
    ...actual,
    useLabels: vi.fn().mockReturnValue({ data: LABELS_MOCK, isLoading: false }),
  }
})

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: `issue-${Math.random().toString(36).slice(2)}`,
    number: 1,
    title: 'Test Issue',
    stage: Stage.Backlog,
    status: IssueStatus.Active,
    projectId: 'proj-1',
    labels: [],
    priority: 'p2',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  }
}

function makeIssues(count: number, overrides: Partial<Issue> = {}): Issue[] {
  return Array.from({ length: count }, (_, i) =>
    makeIssue({
      number: i + 1,
      title: `Issue ${i + 1}`,
      ...overrides,
    }),
  )
}

const mockAgentStatus: AgentStatus = {
  running: false,
  issueId: null,
  issueNumber: null,
  activeAgents: [],
  capacity: { active: 0, max: 2 },
}

describe('Board Query State - URL Serialization', () => {
  describe('parseBoardQuery', () => {
    it('parses empty search string to default state', () => {
      const state = parseBoardQuery('')
      expect(state.priorities).toEqual([])
      expect(state.labels).toEqual([])
      expect(state.search).toBe('')
      expect(state.sort).toBe('priority')
    })

    it('parses priorities from URL', () => {
      const state = parseBoardQuery('priorities=p0,p1')
      expect(state.priorities).toEqual(['p0', 'p1'])
    })

    it('parses labels from URL', () => {
      const state = parseBoardQuery('labels=bug,feature')
      expect(state.labels).toEqual(['bug', 'feature'])
    })

    it('parses search from URL', () => {
      const state = parseBoardQuery('search=login')
      expect(state.search).toBe('login')
    })

    it('parses sort from URL', () => {
      const state = parseBoardQuery('sort=updated')
      expect(state.sort).toBe('updated')
    })

    it('defaults sort to priority when invalid sort value', () => {
      const state = parseBoardQuery('sort=invalid')
      expect(state.sort).toBe('priority')
    })

    it('parses full board state from URL', () => {
      const state = parseBoardQuery('priorities=p0&labels=bug&search=auth&sort=updated')
      expect(state.priorities).toEqual(['p0'])
      expect(state.labels).toEqual(['bug'])
      expect(state.search).toBe('auth')
      expect(state.sort).toBe('updated')
    })

    it('restores state from URL with multiple priorities', () => {
      const state = parseBoardQuery('priorities=p0,p1,p2')
      expect(state.priorities).toEqual(['p0', 'p1', 'p2'])
    })
  })

  describe('serializeBoardQuery', () => {
    it('serializes empty state to empty string', () => {
      const query = serializeBoardQuery({ priorities: [], labels: [], search: '', sort: 'priority' })
      expect(query).toBe('')
    })

    it('serializes priorities', () => {
      const query = serializeBoardQuery({ priorities: ['p0', 'p1'], labels: [], search: '', sort: 'priority' })
      expect(query).toContain('priorities=p0%2Cp1')
    })

    it('serializes labels', () => {
      const query = serializeBoardQuery({ priorities: [], labels: ['bug', 'feature'], search: '', sort: 'priority' })
      expect(query).toContain('labels=bug%2Cfeature')
    })

    it('serializes search', () => {
      const query = serializeBoardQuery({ priorities: [], labels: [], search: 'login', sort: 'priority' })
      expect(query).toContain('search=login')
    })

    it('does not serialize sort when priority (default)', () => {
      const query = serializeBoardQuery({ priorities: [], labels: [], search: '', sort: 'priority' })
      expect(query).not.toContain('sort=')
    })

    it('serializes sort when not priority', () => {
      const query = serializeBoardQuery({ priorities: [], labels: [], search: '', sort: 'updated' })
      expect(query).toContain('sort=updated')
    })

    it('round-trips URL state correctly', () => {
      const originalState: BoardQueryState = {
        priorities: ['p0', 'p1'],
        labels: ['bug'],
        search: 'auth',
        sort: 'updated',
      }
      const query = serializeBoardQuery(originalState)
      const restored = parseBoardQuery(query)
      expect(restored).toEqual(originalState)
    })
  })
})

describe('Board Query State - Filtering', () => {
  describe('applyBoardFilters', () => {
    it('returns all issues when no filters applied', () => {
      const issues = makeIssues(5)
      const state: BoardQueryState = { priorities: [], labels: [], search: '', sort: 'priority' }
      const filtered = applyBoardFilters(issues, state)
      expect(filtered).toHaveLength(5)
    })

    it('filters by single priority', () => {
      const issues = [
        makeIssue({ number: 1, priority: 'p0' }),
        makeIssue({ number: 2, priority: 'p1' }),
        makeIssue({ number: 3, priority: 'p2' }),
      ]
      const state: BoardQueryState = { priorities: ['p0'], labels: [], search: '', sort: 'priority' }
      const filtered = applyBoardFilters(issues, state)
      expect(filtered).toHaveLength(1)
      expect(filtered[0].priority).toBe('p0')
    })

    it('filters by multiple priorities', () => {
      const issues = [
        makeIssue({ number: 1, priority: 'p0' }),
        makeIssue({ number: 2, priority: 'p1' }),
        makeIssue({ number: 3, priority: 'p2' }),
        makeIssue({ number: 4, priority: 'p3' }),
      ]
      const state: BoardQueryState = { priorities: ['p0', 'p1'], labels: [], search: '', sort: 'priority' }
      const filtered = applyBoardFilters(issues, state)
      expect(filtered).toHaveLength(2)
      expect(filtered.every(i => i.priority === 'p0' || i.priority === 'p1')).toBe(true)
    })

    it('filters by single label', () => {
      const issues = [
        makeIssue({ number: 1, labels: ['bug'] }),
        makeIssue({ number: 2, labels: ['feature'] }),
        makeIssue({ number: 3, labels: ['bug', 'docs'] }),
      ]
      const state: BoardQueryState = { priorities: [], labels: ['bug'], search: '', sort: 'priority' }
      const filtered = applyBoardFilters(issues, state)
      expect(filtered).toHaveLength(2)
      expect(filtered.every(i => i.labels.includes('bug'))).toBe(true)
    })

    it('filters by multiple labels (AND logic)', () => {
      const issues = [
        makeIssue({ number: 1, labels: ['bug', 'urgent'] }),
        makeIssue({ number: 2, labels: ['bug'] }),
        makeIssue({ number: 3, labels: ['feature'] }),
      ]
      const state: BoardQueryState = { priorities: [], labels: ['bug', 'urgent'], search: '', sort: 'priority' }
      const filtered = applyBoardFilters(issues, state)
      expect(filtered).toHaveLength(1)
      expect(filtered[0].labels).toContain('bug')
      expect(filtered[0].labels).toContain('urgent')
    })

    it('filters by title search (case-insensitive)', () => {
      const issues = [
        makeIssue({ number: 1, title: 'Login bug' }),
        makeIssue({ number: 2, title: 'Auth error' }),
        makeIssue({ number: 3, title: 'LOGIN form' }),
        makeIssue({ number: 4, title: 'Register page' }),
      ]
      const state: BoardQueryState = { priorities: [], labels: [], search: 'login', sort: 'priority' }
      const filtered = applyBoardFilters(issues, state)
      expect(filtered).toHaveLength(2)
      expect(filtered.every(i => i.title.toLowerCase().includes('login'))).toBe(true)
    })

    it('combines priority, label, and search filters', () => {
      const issues = [
        makeIssue({ number: 1, title: 'Login bug', priority: 'p0', labels: ['bug'] }),
        makeIssue({ number: 2, title: 'Login feature', priority: 'p0', labels: ['feature'] }),
        makeIssue({ number: 3, title: 'Auth bug', priority: 'p1', labels: ['bug'] }),
        makeIssue({ number: 4, title: 'Login bug', priority: 'p2', labels: ['bug'] }),
      ]
      const state: BoardQueryState = {
        priorities: ['p0'],
        labels: ['bug'],
        search: 'login',
        sort: 'priority',
      }
      const filtered = applyBoardFilters(issues, state)
      expect(filtered).toHaveLength(1)
      expect(filtered[0].number).toBe(1)
    })

    it('normalizes missing priority to p2 in filter', () => {
      const issues = [
        makeIssue({ number: 1, priority: undefined as any }),
        makeIssue({ number: 2, priority: 'p2' }),
      ]
      const state: BoardQueryState = { priorities: ['p2'], labels: [], search: '', sort: 'priority' }
      const filtered = applyBoardFilters(issues, state)
      expect(filtered).toHaveLength(2)
    })
  })
})

describe('Board Query State - Sorting', () => {
  describe('deriveBoardColumns', () => {
    it('sorts by priority by default', () => {
      const issues = [
        makeIssue({ number: 1, priority: 'p3' }),
        makeIssue({ number: 2, priority: 'p0' }),
        makeIssue({ number: 3, priority: 'p2' }),
      ]
      const columns = groupIssuesByStage(issues)
      const state: BoardQueryState = { priorities: [], labels: [], search: '', sort: 'priority' }
      const result = deriveBoardColumns(columns, state)
      expect(result[0].issues[0].priority).toBe('p0')
      expect(result[0].issues[1].priority).toBe('p2')
      expect(result[0].issues[2].priority).toBe('p3')
    })

    it('sorts by number desc', () => {
      const issues = [
        makeIssue({ number: 1, priority: 'p2' }),
        makeIssue({ number: 5, priority: 'p2' }),
        makeIssue({ number: 3, priority: 'p2' }),
      ]
      const columns = groupIssuesByStage(issues)
      const state: BoardQueryState = { priorities: [], labels: [], search: '', sort: 'number' }
      const result = deriveBoardColumns(columns, state)
      expect(result[0].issues[0].number).toBe(5)
      expect(result[0].issues[1].number).toBe(3)
      expect(result[0].issues[2].number).toBe(1)
    })

    it('sorts by updated desc', () => {
      const issues = [
        makeIssue({ number: 1, priority: 'p2', updatedAt: '2026-01-01T00:00:00Z' }),
        makeIssue({ number: 2, priority: 'p2', updatedAt: '2026-01-03T00:00:00Z' }),
        makeIssue({ number: 3, priority: 'p2', updatedAt: '2026-01-02T00:00:00Z' }),
      ]
      const columns = groupIssuesByStage(issues)
      const state: BoardQueryState = { priorities: [], labels: [], search: '', sort: 'updated' }
      const result = deriveBoardColumns(columns, state)
      expect(result[0].issues[0].number).toBe(2)
      expect(result[0].issues[1].number).toBe(3)
      expect(result[0].issues[2].number).toBe(1)
    })
  })
})

describe('KanbanBoard Component - Filtered Stage Counts', () => {
  beforeEach(() => {
    Object.defineProperty(window, 'location', {
      value: { search: '', pathname: '/' },
      writable: true,
    })
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders all columns with unfiltered issues', () => {
    const issues = [
      makeIssue({ number: 1, stage: Stage.Backlog }),
      makeIssue({ number: 2, stage: Stage.Backlog }),
      makeIssue({ number: 3, stage: Stage.Plan }),
      makeIssue({ number: 4, stage: Stage.Build }),
    ]
    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
        </MemoryRouter>
      </QueryClientProvider>,
    )

    expect(screen.getAllByText('Backlog').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Plan').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Build').length).toBeGreaterThan(0)
  })

  it('displays filtered issue count after priority filter applied', () => {
    const issues = [
      makeIssue({ number: 1, stage: Stage.Backlog, priority: 'p0' }),
      makeIssue({ number: 2, stage: Stage.Backlog, priority: 'p1' }),
      makeIssue({ number: 3, stage: Stage.Backlog, priority: 'p2' }),
      makeIssue({ number: 4, stage: Stage.Plan, priority: 'p0' }),
    ]

    Object.defineProperty(window, 'location', {
      value: { search: 'priorities=p0', pathname: '/' },
      writable: true,
    })

    const queryClient = new QueryClient()

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
        </MemoryRouter>
      </QueryClientProvider>,
    )

    const backlogElements = screen.getAllByText('Backlog')
    const backlogCol = backlogElements[0].closest('[class*="flex-col"]')
      || backlogElements[0].closest('div')
    expect(backlogCol?.textContent).toContain('1')
  })
})

describe('KanbanBoard Homepage Regression Coverage', () => {
  beforeEach(() => {
    Object.defineProperty(window, 'location', {
      value: { search: '', pathname: '/' },
      writable: true,
    })
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  describe('Desktop layout regression - horizontal multi-column contract at md+', () => {
    it('renders desktop board container with horizontal multi-column layout at md+', () => {
      const issues = [
        makeIssue({ number: 1, stage: Stage.Backlog }),
        makeIssue({ number: 2, stage: Stage.Plan }),
        makeIssue({ number: 3, stage: Stage.Build }),
        makeIssue({ number: 4, stage: Stage.Check }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const desktopBoard = document.querySelector('.hidden.md\\:flex.flex-row')
      expect(desktopBoard).not.toBeNull()
      expect(desktopBoard?.children.length).toBeGreaterThan(0)
    })

    it('does not stack all stage columns vertically in desktop board container', () => {
      const issues = [
        makeIssue({ number: 1, stage: Stage.Backlog }),
        makeIssue({ number: 2, stage: Stage.Plan }),
        makeIssue({ number: 3, stage: Stage.Done }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const desktopBoard = document.querySelector('.hidden.md\\:flex.flex-row')
      expect(desktopBoard).not.toBeNull()
      const stageColumns = desktopBoard!.querySelectorAll('[class*="min-w-"]')
      expect(stageColumns.length).toBeGreaterThanOrEqual(3)
    })
  })

  describe('Needs attention summary - user-action wording', () => {
    it('renders attention summary item with user-action label for approval awaiting issue', () => {
      const approvalAwaitingIssue = makeIssue({
        number: 180,
        title: 'Plan awaits review',
        stage: Stage.Plan,
        status: IssueStatus.Active,
        approvalState: { status: 'awaiting', requestedAt: '2026-01-01T00:00:00Z' } as ApprovalState,
      })
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={[approvalAwaitingIssue]} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const summary = document.querySelector('.bg-amber-50')!
      expect(summary).toBeTruthy()
      expect(within(summary as HTMLElement).getByText(/Needs attention/i)).toBeInTheDocument()
      expect(within(summary as HTMLElement).getByText(/Approval needed/i)).toBeInTheDocument()
      expect(within(summary as HTMLElement).getByText(/#180/i)).toBeInTheDocument()
    })

    it('renders attention summary item with user-action label for interrupted issue', () => {
      const interruptedIssue = makeIssue({
        number: 17,
        title: 'Resume available',
        stage: Stage.Build,
        status: IssueStatus.Interrupted,
      })
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={[interruptedIssue]} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const summary = document.querySelector('.bg-amber-50')!
      expect(summary).toBeTruthy()
      expect(within(summary as HTMLElement).getByText(/Needs attention/i)).toBeInTheDocument()
      expect(within(summary as HTMLElement).getByText(/Interrupted/i)).toBeInTheDocument()
      expect(within(summary as HTMLElement).getByText(/#17/i)).toBeInTheDocument()
    })

    it('renders attention summary item with user-action label for integration failed issue', () => {
      const failedIssue = makeIssue({
        number: 206,
        title: 'merge failed at squash',
        stage: Stage.Integrate,
        status: IssueStatus.Active,
        mergeState: 'build-failed',
      })
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={[failedIssue]} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const summary = document.querySelector('.bg-amber-50')!
      expect(summary).toBeTruthy()
      expect(within(summary as HTMLElement).getByText(/Needs attention/i)).toBeInTheDocument()
      expect(within(summary as HTMLElement).getByText(/Integration failed/i)).toBeInTheDocument()
      expect(within(summary as HTMLElement).getByText(/#206/i)).toBeInTheDocument()
    })

    it('renders integration failed label for blocked integrate issue', () => {
      const failedIssue = makeIssue({
        number: 207,
        title: 'integration blocked by merge conflict',
        stage: Stage.Integrate,
        status: IssueStatus.Blocked,
        blockedReason: 'merge conflict',
      })
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={[failedIssue]} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const summary = document.querySelector('.bg-amber-50')!
      expect(summary).toBeTruthy()
      expect(within(summary as HTMLElement).getByText(/Needs attention/i)).toBeInTheDocument()
      expect(within(summary as HTMLElement).getByText(/Integration failed/i)).toBeInTheDocument()
      expect(within(summary as HTMLElement).queryByText(/Needs action/i)).not.toBeInTheDocument()
      expect(within(summary as HTMLElement).getByText(/#207/i)).toBeInTheDocument()
    })

    it('renders integration failed label for integrate merge conflict state', () => {
      const failedIssue = makeIssue({
        number: 208,
        title: 'integration merge conflict',
        stage: Stage.Integrate,
        status: IssueStatus.Active,
        mergeState: 'conflict',
      })
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={[failedIssue]} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const summary = document.querySelector('.bg-amber-50')!
      expect(summary).toBeTruthy()
      expect(within(summary as HTMLElement).getByText(/Integration failed/i)).toBeInTheDocument()
      expect(within(summary as HTMLElement).queryByText(/Needs action/i)).not.toBeInTheDocument()
      expect(within(summary as HTMLElement).getByText(/#208/i)).toBeInTheDocument()
    })

    it('renders integration failed label for integrate blocked merge state', () => {
      const failedIssue = makeIssue({
        number: 209,
        title: 'integration blocked',
        stage: Stage.Integrate,
        status: IssueStatus.Active,
        mergeState: 'blocked',
      })
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={[failedIssue]} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const summary = document.querySelector('.bg-amber-50')!
      expect(summary).toBeTruthy()
      expect(within(summary as HTMLElement).getByText(/Integration failed/i)).toBeInTheDocument()
      expect(within(summary as HTMLElement).queryByText(/Needs action/i)).not.toBeInTheDocument()
      expect(within(summary as HTMLElement).getByText(/#209/i)).toBeInTheDocument()
    })

    it('does not render attention summary item for completed workflow without local merge state', () => {
      const doneUnmergedIssue = makeIssue({
        number: 42,
        title: 'Completed but not merged',
        stage: Stage.Done,
        status: IssueStatus.Completed,
        mergeState: 'conflict',
      })
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={[doneUnmergedIssue]} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const summary = document.querySelector('.bg-amber-50')
      expect(summary).toBeNull()
    })

    it('does not render attention summary item for done issue with null mergeState', () => {
      const doneUnmergedIssue = makeIssue({
        number: 43,
        title: 'Completed but missing merge result',
        stage: Stage.Done,
        status: IssueStatus.Completed,
        mergeState: null,
      })
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={[doneUnmergedIssue]} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const summary = document.querySelector('.bg-amber-50')
      expect(summary).toBeNull()
    })

    it('renders generic blocked overlay for blocked done issue', () => {
      const doneUnmergedIssue = makeIssue({
        number: 44,
        title: 'Blocked completed issue',
        stage: Stage.Done,
        status: IssueStatus.Blocked,
        mergeState: 'conflict',
        blockedReason: 'Manual intervention required',
      })
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={[doneUnmergedIssue]} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      expect(screen.getAllByText(/Needs Action/i).length).toBeGreaterThan(0)
      expect(screen.queryByText(/Not merged/i)).not.toBeInTheDocument()
    })

    it('renders attention summary item with Needs action label for blocked issue', () => {
      const blockedIssue = makeIssue({
        number: 99,
        title: 'Issue blocked by dependency',
        stage: Stage.Build,
        status: IssueStatus.Blocked,
        blockedReason: 'waiting on #88',
      })
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={[blockedIssue]} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const summary = document.querySelector('.bg-amber-50')!
      expect(summary).toBeTruthy()
      expect(within(summary as HTMLElement).getByText(/Needs attention/i)).toBeInTheDocument()
      expect(within(summary as HTMLElement).getByText(/Needs action/i)).toBeInTheDocument()
      expect(within(summary as HTMLElement).getByText(/#99/i)).toBeInTheDocument()
    })

    it('does not render attention summary when no actionable items exist', () => {
      const normalIssue = makeIssue({
        number: 1,
        title: 'Normal issue',
        stage: Stage.Backlog,
        status: IssueStatus.Active,
      })
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={[normalIssue]} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      expect(screen.queryByText(/Needs attention/i)).not.toBeInTheDocument()
    })
  })

  describe('Mobile compact filters', () => {
    it('keeps secondary filters behind the mobile disclosure by default', () => {
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={makeIssues(2)} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      expect(screen.getByTestId('mobile-filter-toggle')).toBeInTheDocument()
      expect(screen.queryByTestId('mobile-filter-panel')).not.toBeInTheDocument()

      fireEvent.click(screen.getByTestId('mobile-filter-toggle'))

      const panel = screen.getByTestId('mobile-filter-panel')
      expect(within(panel).getByText(/Priority:/i)).toBeInTheDocument()
      expect(within(panel).getByText(/Labels:/i)).toBeInTheDocument()
      expect(within(panel).getByText(/Sort:/i)).toBeInTheDocument()
      expect(within(panel).getByRole('button', { name: 'Updated' })).toBeInTheDocument()
    })
  })

  describe('Label filtering beyond first eight labels', () => {
    it('restores the visible search input from URL state after popstate navigation', async () => {
      Object.defineProperty(window, 'location', {
        value: { search: 'search=current', pathname: '/' },
        writable: true,
      })

      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={[makeIssue({ title: 'Current issue' })]} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const searchInputs = screen.getAllByPlaceholderText('Search titles...') as HTMLInputElement[]
      expect(searchInputs.map((input) => input.value)).toEqual(['current', 'current'])

      Object.defineProperty(window, 'location', {
        value: { search: 'search=restored', pathname: '/' },
        writable: true,
      })

      act(() => {
        window.dispatchEvent(new PopStateEvent('popstate'))
      })

      await waitFor(() => {
        expect(searchInputs.map((input) => input.value)).toEqual(['restored', 'restored'])
      })
    })

    it('can select a label beyond the first eight via label popover search', async () => {
      const issues = [
        makeIssue({ number: 1, labels: ['reliability'] }),
        makeIssue({ number: 2, labels: ['session'] }),
        makeIssue({ number: 3, labels: ['agent'] }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const labelButton = screen.getByText(/Labels:/i)
      fireEvent.click(labelButton)

      let popover: HTMLElement | null = null
      await waitFor(() => {
        popover = document.querySelector('[class*="origin-top-right"]')
        expect(popover).toBeInTheDocument()
      })

      const searchInput = document.querySelector('input[placeholder="Search labels..."]') as HTMLInputElement
      expect(searchInput).toBeInTheDocument()

      fireEvent.change(searchInput, { target: { value: 'reliability' } })

      await waitFor(() => {
        expect(within(popover!).getByText('reliability')).toBeInTheDocument()
      })
    })

    it('updates board counts after selecting a label beyond the first eight', async () => {
      const issues = [
        makeIssue({ number: 1, stage: Stage.Backlog, labels: ['reliability'] }),
        makeIssue({ number: 2, stage: Stage.Backlog, labels: ['bug'] }),
        makeIssue({ number: 3, stage: Stage.Plan, labels: ['session'] }),
        makeIssue({ number: 4, stage: Stage.Build, labels: ['agent'] }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const labelButton = screen.getByText(/Labels:/i)
      fireEvent.click(labelButton)

      await waitFor(() => {
        const popover = document.querySelector('[class*="origin-top-right"]')
        expect(popover).toBeInTheDocument()
      })

      const sessionLabel = within(document.querySelector('[class*="origin-top-right"]') as HTMLElement).getByText('session')
      fireEvent.click(sessionLabel)

      await waitFor(() => {
        const desktopBoard = document.querySelector('.hidden.md\\:flex.flex-row') as HTMLElement
        expect(desktopBoard).toBeInTheDocument()

        const backlogColumn = desktopBoard.children[0] as HTMLElement
        const planColumn = desktopBoard.children[1] as HTMLElement

        expect(backlogColumn.textContent).toContain('Backlog')
        expect(backlogColumn.textContent).toContain('No issues')
        expect(planColumn.textContent).toContain('Plan')
        expect(planColumn.textContent).toContain('#3')
        expect(planColumn.textContent).toContain('session')
      })
    })

    it('reveals all available labels through the searchable label popover', async () => {
      const issues = [
        makeIssue({ number: 1, labels: ['bug'] }),
        makeIssue({ number: 2, labels: ['feature'] }),
      ]
      const queryClient = new QueryClient()

      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      const labelButton = screen.getByText(/Labels:/i)
      fireEvent.click(labelButton)

      await waitFor(() => {
        const popover = document.querySelector('[class*="origin-top-right"]')
        expect(popover).toBeInTheDocument()
      })

      const searchInput = document.querySelector('input[placeholder="Search labels..."]') as HTMLInputElement
      fireEvent.change(searchInput, { target: { value: 'sess' } })

      await waitFor(() => {
        expect(screen.getByText('session')).toBeInTheDocument()
      })

      fireEvent.change(searchInput, { target: { value: 'agen' } })

      await waitFor(() => {
        expect(screen.getByText('agent')).toBeInTheDocument()
      })
    })
  })
})
