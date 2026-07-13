import dagre from 'dagre'
import type { Edge, Node } from '@xyflow/react'
import type { LinkedIssue } from '../../../entities/epic'
import type { ExternalGhostNode, GraphEdge } from './graph'
import { deriveReadiness, type Readiness, statusColors } from './readiness'
import { externalNodeKey, type ExternalNodeKey } from './graph'

export const NODE_WIDTH = 200
export const NODE_HEIGHT = 80
export const GHOST_NODE_WIDTH = 200
export const GHOST_NODE_HEIGHT = 72

export interface MemberNodeData extends Record<string, unknown> {
  kind: 'member'
  issue: LinkedIssue
  readiness: Readiness
  waitingForIssueNumber: number | null
  navigateTo: string | null
}

export interface GhostNodeData extends Record<string, unknown> {
  kind: 'ghost'
  number: number
  title: string
  status: string
  resolved: boolean
  referencedBy: number[]
}

export type GraphNodeData = MemberNodeData | GhostNodeData

export interface BuildLayoutInput {
  linkedIssues: LinkedIssue[]
  edges: GraphEdge[]
  externalGhosts: ExternalGhostNode[]
  navigatePathFor: (issueNumber: number) => string | null
}

export interface BuildLayoutResult {
  nodes: Node<GraphNodeData>[]
  edges: Edge[]
}

export function buildLayout({
  linkedIssues,
  edges,
  externalGhosts,
  navigatePathFor,
}: BuildLayoutInput): BuildLayoutResult {
  const memberNodes = linkedIssues.map<Node<MemberNodeData>>(issue => {
    const derived = deriveReadiness(issue)
    return {
      id: memberNodeId(issue.number),
      type: 'epicDepMember',
      data: {
        kind: 'member',
        issue,
        readiness: derived.readiness,
        waitingForIssueNumber: derived.waitingForIssueNumber,
        navigateTo: navigatePathFor(issue.number),
      },
      position: { x: 0, y: 0 },
    }
  })

  const ghostNodes = externalGhosts.map<Node<GhostNodeData>>(ghost => ({
    id: externalNodeKey(ghost.number),
    type: 'epicDepGhost',
    data: {
      kind: 'ghost',
      number: ghost.number,
      title: ghost.title,
      status: ghost.status,
      resolved: ghost.resolved,
      referencedBy: ghost.referencedBy,
    },
    position: { x: 0, y: 0 },
  }))

  const flowEdges: Edge[] = edges.map((edge) => {
    const source = edge.sourceIsExternal ? externalNodeKey(edge.source) : memberNodeId(edge.source)
    const target = edge.targetIsExternal ? externalNodeKey(edge.target) : memberNodeId(edge.target)
    return {
      id: `${source}->${target}`,
      source,
      target,
      type: 'smoothstep',
      animated: false,
    }
  })

  const g = new dagre.graphlib.Graph<{}>({ multigraph: false, compound: false })
  g.setGraph({
    rankdir: 'TB',
    nodesep: 32,
    ranksep: 56,
    edgesep: 16,
    marginx: 24,
    marginy: 24,
  })
  g.setDefaultEdgeLabel(() => ({}))

  for (const node of memberNodes) {
    g.setNode(node.id, { width: NODE_WIDTH, height: NODE_HEIGHT })
  }
  for (const node of ghostNodes) {
    g.setNode(node.id, { width: GHOST_NODE_WIDTH, height: GHOST_NODE_HEIGHT })
  }
  for (const edge of flowEdges) {
    g.setEdge(edge.source, edge.target)
  }

  dagre.layout(g)

  const positionedMemberNodes: Node<MemberNodeData>[] = memberNodes.map((node) => {
    const dagreNode = g.node(node.id)
    return {
      ...node,
      position: {
        x: dagreNode.x - NODE_WIDTH / 2,
        y: dagreNode.y - NODE_HEIGHT / 2,
      },
    }
  })

  const positionedGhostNodes: Node<GhostNodeData>[] = ghostNodes.map((node) => {
    const dagreNode = g.node(node.id)
    return {
      ...node,
      position: {
        x: dagreNode.x - GHOST_NODE_WIDTH / 2,
        y: dagreNode.y - GHOST_NODE_HEIGHT / 2,
      },
    }
  })

  return {
    nodes: [...positionedMemberNodes, ...positionedGhostNodes],
    edges: flowEdges,
  }
}

export function memberNodeId(number: number): string {
  return `member-${number}`
}

export function isMemberNodeId(id: string): boolean {
  return id.startsWith('member-')
}

export function parseMemberNodeNumber(id: string): number | null {
  if (!isMemberNodeId(id)) return null
  const rest = id.slice('member-'.length)
  const n = Number(rest)
  return Number.isFinite(n) ? n : null
}

export function isExternalNodeId(id: string): id is ExternalNodeKey {
  return id.startsWith('ext-')
}

export { statusColors }
