import type { LinkedIssue } from '../../../entities/epic'

export interface GraphEdge {
  source: number
  target: number
  sourceIsExternal: boolean
  targetIsExternal: boolean
}

export interface ExternalGhostNode {
  kind: 'ghost'
  number: number
  title: string
  status: string
  resolved: boolean
  referencedBy: number[]
}

export interface InternalNodeReference {
  kind: 'member'
  issue: LinkedIssue
}

export type ExternalNodeKey = `ext-${number}`

export function externalNodeKey(number: number): ExternalNodeKey {
  return `ext-${number}` as ExternalNodeKey
}

export interface GraphBuildResult {
  edges: GraphEdge[]
  externalGhosts: ExternalGhostNode[]
}

export function buildGraphEdges(linkedIssues: LinkedIssue[]): GraphBuildResult {
  const memberNumbers = new Set(linkedIssues.map(issue => issue.number))
  const ghostByNumber = new Map<number, ExternalGhostNode>()
  const edges: GraphEdge[] = []

  for (const issue of linkedIssues) {
    const prereqNumbers = Array.isArray(issue.prerequisiteNumbers) ? issue.prerequisiteNumbers : []
    for (const prereqNumber of prereqNumbers) {
      const sourceIsExternal = !memberNumbers.has(prereqNumber)
      if (sourceIsExternal) {
        const ref = findExternalRef(issue, prereqNumber)
        const existing = ghostByNumber.get(prereqNumber)
        if (existing) {
          if (!existing.referencedBy.includes(issue.number)) {
            existing.referencedBy.push(issue.number)
          }
          if (!existing.resolved && ref) {
            existing.title = ref.title
            existing.status = ref.status
            existing.resolved = true
          }
        } else {
          ghostByNumber.set(prereqNumber, {
            kind: 'ghost',
            number: prereqNumber,
            title: ref?.title ?? '',
            status: ref?.status ?? '',
            resolved: ref !== null,
            referencedBy: [issue.number],
          })
        }
      }
      edges.push({
        source: prereqNumber,
        target: issue.number,
        sourceIsExternal,
        targetIsExternal: false,
      })
    }
  }

  for (const linkedIssue of linkedIssues) {
    for (const ghost of linkedIssue.externalPrerequisites ?? []) {
      const existing = ghostByNumber.get(ghost.number)
      if (existing) {
        if (!existing.resolved) {
          existing.title = ghost.title
          existing.status = ghost.status
          existing.resolved = true
        }
      } else {
        ghostByNumber.set(ghost.number, {
          kind: 'ghost',
          number: ghost.number,
          title: ghost.title,
          status: ghost.status,
          resolved: true,
          referencedBy: [],
        })
      }
    }
  }

  return {
    edges,
    externalGhosts: Array.from(ghostByNumber.values()).sort((a, b) => a.number - b.number),
  }
}

function findExternalRef(issue: LinkedIssue, prereqNumber: number): LinkedIssue['externalPrerequisites'][number] | null {
  const externals = issue.externalPrerequisites ?? []
  return externals.find(ext => ext.number === prereqNumber) ?? null
}

export function detectCycle(edges: GraphEdge[]): boolean {
  const adjacency = new Map<number, number[]>()
  for (const edge of edges) {
    const list = adjacency.get(edge.source)
    if (list) {
      list.push(edge.target)
    } else {
      adjacency.set(edge.source, [edge.target])
    }
  }

  const visiting = new Set<number>()
  const visited = new Set<number>()

  function dfs(node: number): boolean {
    if (visiting.has(node)) return true
    if (visited.has(node)) return false
    visiting.add(node)
    const successors = adjacency.get(node) ?? []
    for (const next of successors) {
      if (dfs(next)) return true
    }
    visiting.delete(node)
    visited.add(node)
    return false
  }

  for (const node of adjacency.keys()) {
    if (dfs(node)) return true
  }
  return false
}
