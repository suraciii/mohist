import type { Issue } from '../../../entities/issue'
import type { Repository } from '../../../entities/project'
import { ApiError } from '../../../shared/api/client'

const ASSIGNMENT_ERROR_CODES = [
  'repository_not_found',
  'parent_not_found',
  'parent_ineligible',
  'parent_is_sub_issue',
] as const

export type CreateIssueAssignmentErrorCode = (typeof ASSIGNMENT_ERROR_CODES)[number]

export function isEligibleParentCandidate(issue: Pick<Issue, 'canBeParent'>): boolean {
  return issue.canBeParent === true
}

export function deriveEligibleParentCandidates(issues: Issue[] | null | undefined): Issue[] {
  if (!issues) return []
  return issues
    .filter(isEligibleParentCandidate)
    .slice()
    .sort((a, b) => a.number - b.number)
}

export function findDefaultRepository(repositories: Repository[] | null | undefined): Repository | null {
  if (!repositories || repositories.length === 0) return null
  const match = repositories.find((repo) => repo.isDefault)
  if (match) return match
  return repositories[0] ?? null
}

export function pickInitialRepositoryName(repositories: Repository[] | null | undefined, currentName: string | null): string | null {
  if (!repositories || repositories.length === 0) return null
  const names = new Set(repositories.map((repo) => repo.name))
  if (currentName && names.has(currentName)) return currentName
  const defaultRepo = findDefaultRepository(repositories)
  return defaultRepo ? defaultRepo.name : null
}

export interface CreateIssueAssignmentError {
  code: CreateIssueAssignmentErrorCode | null
  message: string
  isAssignment: boolean
}

export function mapCreateIssueError(err: unknown): CreateIssueAssignmentError {
  const fallback = (message: string): CreateIssueAssignmentError => ({ code: null, message, isAssignment: false })
  if (err instanceof ApiError) {
    if (err.code && ASSIGNMENT_ERROR_CODES.includes(err.code as CreateIssueAssignmentErrorCode)) {
      return {
        code: err.code as CreateIssueAssignmentErrorCode,
        message: assignmentErrorMessage(err.code as CreateIssueAssignmentErrorCode, err.message),
        isAssignment: true,
      }
    }
    return fallback(err.message || 'Failed to create issue')
  }
  if (err instanceof Error) {
    return fallback(err.message || 'Failed to create issue')
  }
  return fallback('Failed to create issue')
}

function assignmentErrorMessage(code: CreateIssueAssignmentErrorCode, serverMessage?: string): string {
  const trimmed = serverMessage?.trim()
  switch (code) {
    case 'repository_not_found':
      return trimmed && trimmed.length > 0
        ? `Repository unavailable: ${trimmed}. Pick a repository declared by the project.`
        : 'The selected repository is no longer declared by this project.'
    case 'parent_not_found':
      return trimmed && trimmed.length > 0
        ? `Parent issue unavailable: ${trimmed}. Choose another parent or leave it empty.`
        : 'The selected parent issue could not be found. Choose another parent or leave it empty.'
    case 'parent_ineligible':
      return trimmed && trimmed.length > 0
        ? `Parent is no longer eligible: ${trimmed}. The parent must be in backlog with no workflow started.`
        : 'The selected parent is no longer eligible. Parents must be in backlog with no workflow started.'
    case 'parent_is_sub_issue':
      return trimmed && trimmed.length > 0
        ? `Sub-issues cannot be used as parents: ${trimmed}`
        : 'Sub-issues cannot be used as parents.'
  }
}
