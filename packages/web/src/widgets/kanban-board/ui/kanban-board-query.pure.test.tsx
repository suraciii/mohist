import '@testing-library/jest-dom'
import { describe, expect, it } from 'vitest'
import {
  parseBoardQuery,
  serializeBoardQuery,
  deriveBoardColumns,
  applyBoardFilters,
  type BoardQueryState,
} from '../model/board-query'
import { groupIssuesByStage } from '../model/kanban-grouping'
import { makeIssue, makeIssues } from './_kanbanBoardQueryTestUtils'

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
      const state = parseBoardQuery('labels=stream=frontend&labels=module=auth')
      expect(state.labels).toEqual(['stream=frontend', 'module=auth'])
    })

    it('parses legacy comma-separated labels from URL', () => {
      const state = parseBoardQuery('labels=stream=frontend,module=auth')
      expect(state.labels).toEqual(['stream=frontend', 'module=auth'])
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
      const state = parseBoardQuery('priorities=p0&labels=stream=frontend&search=auth&sort=updated')
      expect(state.priorities).toEqual(['p0'])
      expect(state.labels).toEqual(['stream=frontend'])
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
      const query = serializeBoardQuery({ priorities: [], labels: ['stream=frontend', 'module=auth'], search: '', sort: 'priority' })
      expect(query).toContain('labelMode=repeated')
      expect(query).toContain('labels=stream%3Dfrontend')
      expect(query).toContain('labels=module%3Dauth')
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
        labels: ['stream=front,end'],
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

    it('filters by single key=value label', () => {
      const issues = [
        makeIssue({ number: 1, labels: { kind: 'bug' } }),
        makeIssue({ number: 2, labels: { kind: 'feature' } }),
        makeIssue({ number: 3, labels: { kind: 'bug', area: 'docs' } }),
      ]
      const state: BoardQueryState = { priorities: [], labels: ['kind=bug'], search: '', sort: 'priority' }
      const filtered = applyBoardFilters(issues, state)
      expect(filtered).toHaveLength(2)
      expect(filtered.every(i => i.labels.kind === 'bug')).toBe(true)
    })

    it('filters by multiple key=value labels (AND logic)', () => {
      const issues = [
        makeIssue({ number: 1, labels: { kind: 'bug', priority: 'urgent' } }),
        makeIssue({ number: 2, labels: { kind: 'bug' } }),
        makeIssue({ number: 3, labels: { kind: 'feature' } }),
      ]
      const state: BoardQueryState = { priorities: [], labels: ['kind=bug', 'priority=urgent'], search: '', sort: 'priority' }
      const filtered = applyBoardFilters(issues, state)
      expect(filtered).toHaveLength(1)
      expect(filtered[0].labels.kind).toBe('bug')
      expect(filtered[0].labels.priority).toBe('urgent')
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
        makeIssue({ number: 1, title: 'Login bug', priority: 'p0', labels: { kind: 'bug' } }),
        makeIssue({ number: 2, title: 'Login feature', priority: 'p0', labels: { kind: 'feature' } }),
        makeIssue({ number: 3, title: 'Auth bug', priority: 'p1', labels: { kind: 'bug' } }),
        makeIssue({ number: 4, title: 'Login bug', priority: 'p2', labels: { kind: 'bug' } }),
      ]
      const state: BoardQueryState = {
        priorities: ['p0'],
        labels: ['kind=bug'],
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

    it('excludes issues whose key matches but value differs from the key=value filter', () => {
      const issues = [
        makeIssue({ number: 1, labels: { stream: 'frontend' } }),
        makeIssue({ number: 2, labels: { stream: 'backend' } }),
        makeIssue({ number: 3, labels: { stream: 'frontend', module: 'auth' } }),
      ]
      const state: BoardQueryState = { priorities: [], labels: ['stream=frontend'], search: '', sort: 'priority' }
      const filtered = applyBoardFilters(issues, state)
      const numbers = filtered.map((i) => i.number).sort()
      expect(numbers).toEqual([1, 3])
    })

    it('requires every selected key=value token to be present (AND across keys)', () => {
      const issues = [
        makeIssue({ number: 1, labels: { stream: 'frontend', module: 'auth' } }),
        makeIssue({ number: 2, labels: { stream: 'frontend' } }),
        makeIssue({ number: 3, labels: { stream: 'backend', module: 'auth' } }),
        makeIssue({ number: 4, labels: { module: 'auth' } }),
      ]
      const state: BoardQueryState = {
        priorities: [],
        labels: ['stream=frontend', 'module=auth'],
        search: '',
        sort: 'priority',
      }
      const filtered = applyBoardFilters(issues, state)
      expect(filtered).toHaveLength(1)
      expect(filtered[0].number).toBe(1)
    })

    it('handles label values that contain an = character by splitting on the first = only', () => {
      const issues = [
        makeIssue({ number: 1, labels: { stream: 'name=value' } }),
        makeIssue({ number: 2, labels: { stream: 'frontend' } }),
      ]
      const state: BoardQueryState = {
        priorities: [],
        labels: ['stream=name=value'],
        search: '',
        sort: 'priority',
      }
      const filtered = applyBoardFilters(issues, state)
      expect(filtered).toHaveLength(1)
      expect(filtered[0].number).toBe(1)
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
