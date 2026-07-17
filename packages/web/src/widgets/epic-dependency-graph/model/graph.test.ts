import { describe, it, expect } from 'vitest'
import { buildGraphEdges, detectCycle } from './graph'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../../entities/issue/@x/types'
import type { LinkedIssue } from '../../../entities/epic/model/types'

function makeLinkedIssue(overrides: Partial<LinkedIssue> = {}): LinkedIssue {
  return {
    number: 1,
    title: 'Issue',
    status: IssueStatus.Backlog,
    stage: WorkflowStage.Plan,
    health: IssueHealth.Active,
    priority: 'p2',
    canStart: false,
    startBlocker: null,
    prerequisiteNumbers: [],
    externalPrerequisites: [],
    ...overrides,
  }
}

describe('buildGraphEdges', () => {
  it('emits a directed edge from prereq to dependent (A→B where B declares A)', () => {
    const a = makeLinkedIssue({ number: 1 })
    const b = makeLinkedIssue({ number: 2, prerequisiteNumbers: [1] })
    const result = buildGraphEdges([a, b])
    expect(result.edges).toHaveLength(1)
    expect(result.edges[0]).toEqual({
      source: 1,
      target: 2,
      sourceIsExternal: false,
      targetIsExternal: false,
    })
  })

  it('emits edges from each prerequisite to the dependent (one dependent, multiple prereqs)', () => {
    const a = makeLinkedIssue({ number: 1 })
    const b = makeLinkedIssue({ number: 2 })
    const c = makeLinkedIssue({ number: 3, prerequisiteNumbers: [1, 2] })
    const result = buildGraphEdges([a, b, c])
    expect(result.edges).toHaveLength(2)
    const pairs = result.edges.map(e => `${e.source}->${e.target}`).sort()
    expect(pairs).toEqual(['1->3', '2->3'])
  })

  it('marks edges whose source is not in epic membership as external', () => {
    const b = makeLinkedIssue({ number: 2, prerequisiteNumbers: [99] })
    const result = buildGraphEdges([b])
    expect(result.edges[0]).toEqual({
      source: 99,
      target: 2,
      sourceIsExternal: true,
      targetIsExternal: false,
    })
  })

  it('builds a ghost node summary for external prereqs referenced from issue.externalPrerequisites', () => {
    const b = makeLinkedIssue({
      number: 2,
      prerequisiteNumbers: [99],
      externalPrerequisites: [{ number: 99, title: 'Out-of-epic task', stage: 'plan', status: 'active' }],
    })
    const result = buildGraphEdges([b])
    expect(result.externalGhosts).toHaveLength(1)
    expect(result.externalGhosts[0]).toEqual({
      kind: 'ghost',
      number: 99,
      title: 'Out-of-epic task',
      status: 'active',
      resolved: true,
      referencedBy: [2],
    })
  })

  it('builds an unresolved ghost node when the prereq number is not in any external summary', () => {
    const b = makeLinkedIssue({ number: 2, prerequisiteNumbers: [404] })
    const result = buildGraphEdges([b])
    expect(result.externalGhosts).toHaveLength(1)
    expect(result.externalGhosts[0].number).toBe(404)
    expect(result.externalGhosts[0].resolved).toBe(false)
    expect(result.externalGhosts[0].title).toBe('')
    expect(result.externalGhosts[0].status).toBe('')
  })

  it('emits no ghost when all prereqs are in-epic', () => {
    const a = makeLinkedIssue({ number: 1 })
    const b = makeLinkedIssue({ number: 2, prerequisiteNumbers: [1] })
    const result = buildGraphEdges([a, b])
    expect(result.externalGhosts).toHaveLength(0)
  })

  it('records multiple referrers when several linked issues share an external prereq', () => {
    const a = makeLinkedIssue({ number: 1, prerequisiteNumbers: [99] })
    const b = makeLinkedIssue({ number: 2, prerequisiteNumbers: [99] })
    const result = buildGraphEdges([a, b])
    expect(result.externalGhosts).toHaveLength(1)
    expect(result.externalGhosts[0].referencedBy).toEqual([1, 2])
  })

  it('tolerates undefined prerequisiteNumbers by treating them as an empty list', () => {
    const a = makeLinkedIssue({ number: 1 })
    const result = buildGraphEdges([a])
    expect(result.edges).toHaveLength(0)
    expect(result.externalGhosts).toHaveLength(0)
  })
})

describe('detectCycle', () => {
  it('returns false for a simple linear chain A→B→C', () => {
    const result = buildGraphEdges([
      makeLinkedIssue({ number: 1 }),
      makeLinkedIssue({ number: 2, prerequisiteNumbers: [1] }),
      makeLinkedIssue({ number: 3, prerequisiteNumbers: [2] }),
    ])
    expect(detectCycle(result.edges)).toBe(false)
  })

  it('returns false for a DAG with diamond shape (no cycle)', () => {
    const result = buildGraphEdges([
      makeLinkedIssue({ number: 1 }),
      makeLinkedIssue({ number: 2, prerequisiteNumbers: [1] }),
      makeLinkedIssue({ number: 3, prerequisiteNumbers: [1] }),
      makeLinkedIssue({ number: 4, prerequisiteNumbers: [2, 3] }),
    ])
    expect(detectCycle(result.edges)).toBe(false)
  })

  it('returns true for a cycle A→B→A', () => {
    const edges = [
      { source: 1, target: 2, sourceIsExternal: false, targetIsExternal: false },
      { source: 2, target: 1, sourceIsExternal: false, targetIsExternal: false },
    ]
    expect(detectCycle(edges)).toBe(true)
  })

  it('returns true for a cycle A→B→C→A', () => {
    const edges = [
      { source: 1, target: 2, sourceIsExternal: false, targetIsExternal: false },
      { source: 2, target: 3, sourceIsExternal: false, targetIsExternal: false },
      { source: 3, target: 1, sourceIsExternal: false, targetIsExternal: false },
    ]
    expect(detectCycle(edges)).toBe(true)
  })

  it('returns true for a self-loop', () => {
    const edges = [{ source: 1, target: 1, sourceIsExternal: false, targetIsExternal: false }]
    expect(detectCycle(edges)).toBe(true)
  })

  it('returns false for an empty edge set', () => {
    expect(detectCycle([])).toBe(false)
  })

  it('returns false for an isolated node with no edges', () => {
    const result = buildGraphEdges([
      makeLinkedIssue({ number: 1 }),
      makeLinkedIssue({ number: 2 }),
    ])
    expect(detectCycle(result.edges)).toBe(false)
  })
})
