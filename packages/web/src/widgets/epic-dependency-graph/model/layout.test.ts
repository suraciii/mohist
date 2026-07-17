import { describe, it, expect } from 'vitest'
import { buildLayout, memberNodeId, isMemberNodeId, parseMemberNodeNumber, isExternalNodeId, NODE_WIDTH, NODE_HEIGHT } from './layout'
import { buildGraphEdges } from './graph'
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

describe('buildLayout', () => {
  it('produces one positioned node per linked issue', () => {
    const a = makeLinkedIssue({ number: 1 })
    const b = makeLinkedIssue({ number: 2, prerequisiteNumbers: [1] })
    const { edges: graphEdges } = buildGraphEdges([a, b])
    const layout = buildLayout({
      linkedIssues: [a, b],
      edges: graphEdges,
      externalGhosts: [],
      navigatePathFor: n => `/issues/${n}`,
    })
    expect(layout.nodes).toHaveLength(2)
    for (const n of layout.nodes) {
      expect(typeof n.position.x).toBe('number')
      expect(typeof n.position.y).toBe('number')
    }
  })

  it('positions each member node anchored so its center is the dagre coordinate (width subtracted)', () => {
    const a = makeLinkedIssue({ number: 1 })
    const { nodes } = buildLayout({
      linkedIssues: [a],
      edges: [],
      externalGhosts: [],
      navigatePathFor: n => `/issues/${n}`,
    })
    const member = nodes.find(n => n.id === memberNodeId(1))!
    expect(member).toBeTruthy()
    expect(member.position.x).toBeGreaterThanOrEqual(-NODE_WIDTH)
    expect(member.position.y).toBeGreaterThanOrEqual(-NODE_HEIGHT)
  })

  it('produces a member node carrying readiness and waitingForIssueNumber', () => {
    const a = makeLinkedIssue({
      number: 1,
      status: IssueStatus.Backlog,
      canStart: false,
      startBlocker: { kind: 'waiting-for', issue: { number: 7, title: 'X' } },
    })
    const { nodes } = buildLayout({
      linkedIssues: [a],
      edges: [],
      externalGhosts: [],
      navigatePathFor: n => `/issues/${n}`,
    })
    const member = nodes.find(n => n.id === memberNodeId(1))
    expect(member).toBeTruthy()
    if (member && member.data.kind === 'member') {
      expect(member.data.readiness).toBe('waiting')
      expect(member.data.waitingForIssueNumber).toBe(7)
    } else {
      throw new Error('expected member node data')
    }
  })

  it('produces a ghost node for external prereqs with the resolved flag and number', () => {
    const a = makeLinkedIssue({
      number: 1,
      prerequisiteNumbers: [99],
      externalPrerequisites: [{ number: 99, title: 'Out-of-epic', stage: 'plan', status: 'active' }],
    })
    const { edges: graphEdges, externalGhosts } = buildGraphEdges([a])
    const { nodes } = buildLayout({
      linkedIssues: [a],
      edges: graphEdges,
      externalGhosts,
      navigatePathFor: n => `/issues/${n}`,
    })
    const ghost = nodes.find(n => n.id === 'ext-99')
    expect(ghost).toBeTruthy()
    if (ghost && ghost.data.kind === 'ghost') {
      expect(ghost.data.number).toBe(99)
      expect(ghost.data.title).toBe('Out-of-epic')
      expect(ghost.data.status).toBe('active')
      expect(ghost.data.resolved).toBe(true)
      expect(ghost.data.referencedBy).toEqual([1])
    } else {
      throw new Error('expected ghost node data')
    }
  })

  it('produces an unresolved ghost with empty title and status when the prereq has no external summary', () => {
    const a = makeLinkedIssue({ number: 1, prerequisiteNumbers: [404] })
    const { edges: graphEdges, externalGhosts } = buildGraphEdges([a])
    const { nodes } = buildLayout({
      linkedIssues: [a],
      edges: graphEdges,
      externalGhosts,
      navigatePathFor: n => `/issues/${n}`,
    })
    const ghost = nodes.find(n => n.id === 'ext-404')
    expect(ghost).toBeTruthy()
    if (ghost && ghost.data.kind === 'ghost') {
      expect(ghost.data.resolved).toBe(false)
      expect(ghost.data.title).toBe('')
      expect(ghost.data.status).toBe('')
      expect(ghost.data.referencedBy).toEqual([1])
    } else {
      throw new Error('expected ghost node data')
    }
  })

  it('produces one edge per prerequisite relationship', () => {
    const a = makeLinkedIssue({ number: 1 })
    const b = makeLinkedIssue({ number: 2 })
    const c = makeLinkedIssue({ number: 3, prerequisiteNumbers: [1, 2] })
    const { edges: graphEdges } = buildGraphEdges([a, b, c])
    const { edges } = buildLayout({
      linkedIssues: [a, b, c],
      edges: graphEdges,
      externalGhosts: [],
      navigatePathFor: n => `/issues/${n}`,
    })
    expect(edges).toHaveLength(2)
    const ids = edges.map(e => `${e.source}->${e.target}`).sort()
    expect(ids).toEqual([`member-1->member-3`, `member-2->member-3`])
  })

  it('routes edges from ghost nodes when the source is external', () => {
    const a = makeLinkedIssue({
      number: 1,
      prerequisiteNumbers: [99],
      externalPrerequisites: [{ number: 99, title: 'Out-of-epic', stage: 'plan', status: 'active' }],
    })
    const { edges: graphEdges, externalGhosts } = buildGraphEdges([a])
    const { edges } = buildLayout({
      linkedIssues: [a],
      edges: graphEdges,
      externalGhosts,
      navigatePathFor: n => `/issues/${n}`,
    })
    expect(edges).toHaveLength(1)
    expect(edges[0].source).toBe('ext-99')
    expect(edges[0].target).toBe('member-1')
    expect(edges[0].type).toBe('smoothstep')
  })
})

describe('memberNodeId helpers', () => {
  it('memberNodeId formats the node id as "member-<n>"', () => {
    expect(memberNodeId(7)).toBe('member-7')
  })

  it('isMemberNodeId and parseMemberNodeNumber round-trip', () => {
    expect(isMemberNodeId('member-42')).toBe(true)
    expect(parseMemberNodeNumber('member-42')).toBe(42)
    expect(isMemberNodeId('ext-42')).toBe(false)
    expect(parseMemberNodeNumber('ext-42')).toBeNull()
    expect(parseMemberNodeNumber('member-notanumber')).toBeNull()
  })

  it('isExternalNodeId recognizes the ext- prefix', () => {
    expect(isExternalNodeId('ext-1')).toBe(true)
    expect(isExternalNodeId('member-1')).toBe(false)
  })
})
