import { parseLabelSearchParams, parseLabelToken, serializeLabelSearchParams, type Issue } from '../../../entities/issue'
import type { Column } from './kanban-grouping'

export type SortMode = 'priority' | 'number' | 'updated'

export interface BoardQueryState {
  priorities: string[]
  labels: string[]
  search: string
  sort: SortMode
  repository: string | null
}

export function parseBoardQuery(search: string): BoardQueryState {
  const params = new URLSearchParams(search)
  const priorities = params.get('priorities')
  const searchParam = params.get('search')
  const sortParam = params.get('sort')
  const repositoryParam = params.get('repository')

  return {
    priorities: priorities ? priorities.split(',').filter(Boolean) : [],
    labels: parseLabelSearchParams(params, search),
    search: searchParam ?? '',
    sort: (sortParam === 'priority' || sortParam === 'number' || sortParam === 'updated')
      ? sortParam
      : 'priority',
    repository: repositoryParam && repositoryParam.length > 0 ? repositoryParam : null,
  }
}

export function serializeBoardQuery(state: BoardQueryState): string {
  const params = new URLSearchParams()
  if (state.priorities.length > 0) {
    params.set('priorities', state.priorities.join(','))
  }
  if (state.labels.length > 0) {
    serializeLabelSearchParams(params, state.labels)
  }
  if (state.search) {
    params.set('search', state.search)
  }
  if (state.sort !== 'priority') {
    params.set('sort', state.sort)
  }
  if (state.repository) {
    params.set('repository', state.repository)
  }
  return params.toString()
}

export function updateBoardURL(state: BoardQueryState): void {
  const search = serializeBoardQuery(state)
  const newUrl = search ? `${window.location.pathname}?${search}` : window.location.pathname
  window.history.pushState({}, '', newUrl)
}

function normalizePriority(p: string | null | undefined): string {
  if (!p) return 'p2'
  if (p.startsWith('p')) return p
  return 'p2'
}

const PRIORITY_ORDER = ['p0', 'p1', 'p2', 'p3', 'p4']
function priorityIndex(p: string): number {
  return PRIORITY_ORDER.indexOf(p)
}

function sortIssues(issues: Issue[], mode: SortMode): Issue[] {
  return [...issues].sort((a, b) => {
    if (mode === 'number') {
      return b.number - a.number
    }
    if (mode === 'updated') {
      return new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime()
    }
    // priority (default)
    const pa = normalizePriority(a.priority)
    const pb = normalizePriority(b.priority)
    const piDiff = priorityIndex(pa) - priorityIndex(pb)
    if (piDiff !== 0) return piDiff
    // tie-breaker: updatedAt desc
    const updatedDiff = new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime()
    if (updatedDiff !== 0) return updatedDiff
    // tie-breaker: number desc
    return b.number - a.number
  })
}

export function issueMatchesLabelTokens(labels: Record<string, string> | undefined | null, tokens: string[]): boolean {
  if (!tokens || tokens.length === 0) return true
  const safeLabels = labels ?? {}
  return tokens.every((token) => {
    const parsed = parseLabelToken(token)
    if (!parsed) return false
    return safeLabels[parsed.key] === parsed.value
  })
}

export function issueRepositoryName(issue: Issue): string | null {
  const resolved = issue.repository?.name
  if (resolved && resolved.length > 0) return resolved
  const persisted = issue.repositoryName
  if (persisted && persisted.length > 0) return persisted
  return null
}

export function deriveRepositoryOptions(issues: Issue[]): string[] {
  const seen = new Set<string>()
  for (const issue of issues) {
    const name = issueRepositoryName(issue)
    if (name) seen.add(name)
  }
  return Array.from(seen).sort((a, b) => a.localeCompare(b))
}

export function applyBoardFilters(
  issues: Issue[],
  state: BoardQueryState,
): Issue[] {
  let result = issues

  if (state.priorities.length > 0) {
    result = result.filter((issue) => {
      const p = normalizePriority(issue.priority)
      return state.priorities.includes(p)
    })
  }

  if (state.labels.length > 0) {
    result = result.filter((issue) => issueMatchesLabelTokens(issue.labels, state.labels))
  }

  if (state.search.trim()) {
    const q = state.search.trim().toLowerCase()
    result = result.filter((issue) =>
      issue.title.toLowerCase().includes(q),
    )
  }

  if (state.repository) {
    const target = state.repository
    result = result.filter((issue) => issueRepositoryName(issue) === target)
  }

  return result
}

export function deriveBoardColumns(
  columns: Column[],
  state: BoardQueryState,
): Column[] {
  return columns.map((col) => {
    const filtered = applyBoardFilters(col.issues, state)
    return {
      ...col,
      issues: sortIssues(filtered, state.sort),
    }
  })
}
