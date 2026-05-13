// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi, beforeEach } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { KanbanBoard } from './KanbanBoard'
import { Stage, IssueStatus } from '../lib/types'
import type { Issue, AgentStatus } from '../lib/types'
import {
  parseBoardQuery,
  serializeBoardQuery,
  deriveBoardColumns,
  applyBoardFilters,
  type BoardQueryState,
} from '../lib/board-query'
import { groupIssuesByStage } from '../lib/kanban-grouping'

vi.mock('../hooks/useQueries', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../hooks/useQueries')>()
  return {
    ...actual,
    useLabels: vi.fn().mockReturnValue({ data: ['bug', 'feature', 'docs'], isLoading: false }),
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
  maxConcurrentAgents: 2,
  queueDepth: 0,
  waitingQuestions: [],
  recoverableIssues: [],
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
      value: { search: '' },
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
      value: { search: 'priorities=p0' },
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