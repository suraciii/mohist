import { describe, expect, it } from 'vitest'
import { ApiError } from '../../../shared/api/client'
import { IssueHealth, IssueStatus, type Issue } from '../../../entities/issue'
import type { Repository } from '../../../entities/project'
import {
  deriveEligibleParentCandidates,
  findDefaultRepository,
  isEligibleParentCandidate,
  mapCreateIssueError,
  pickInitialRepositoryName,
} from './assignment'

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    number: 1,
    title: 'Sample',
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    projectId: 'proj_test',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isDraft: false,
    canStart: true,
    canBeParent: true,
    blocker: null,
    ...overrides,
  }
}

describe('isEligibleParentCandidate', () => {
  it('accepts an issue the server marks as eligible', () => {
    expect(isEligibleParentCandidate({ canBeParent: true })).toBe(true)
  })

  it('rejects an issue the server marks as ineligible', () => {
    expect(isEligibleParentCandidate({ canBeParent: false })).toBe(false)
  })
})

describe('deriveEligibleParentCandidates', () => {
  it('excludes server-ineligible issues and sorts the remainder ascending by number', () => {
    const candidates = deriveEligibleParentCandidates([
      makeIssue({ number: 5, canBeParent: false }),
      makeIssue({ number: 2, canBeParent: true }),
      makeIssue({ number: 9, canBeParent: false }),
      makeIssue({ number: 4, canBeParent: false }),
      makeIssue({ number: 1, canBeParent: true }),
    ])

    expect(candidates.map((issue) => issue.number)).toEqual([1, 2])
  })

  it('returns an empty array when the input is null, undefined, or empty', () => {
    expect(deriveEligibleParentCandidates(null)).toEqual([])
    expect(deriveEligibleParentCandidates(undefined)).toEqual([])
    expect(deriveEligibleParentCandidates([])).toEqual([])
  })
})

describe('findDefaultRepository', () => {
  const server = { name: 'server', gitUrl: 'git@example.com:server.git', baseBranch: 'main', isDefault: true }
  const web = { name: 'web', gitUrl: 'git@example.com:web.git', baseBranch: 'main', isDefault: false }
  const infra = { name: 'infra', gitUrl: 'git@example.com:infra.git', baseBranch: 'main', isDefault: false }

  it('returns the single default repository when present', () => {
    expect(findDefaultRepository([server, web])?.name).toBe('server')
  })

  it('falls back to the first repository when no default is declared', () => {
    expect(findDefaultRepository([web, infra])?.name).toBe('web')
  })

  it('returns null for an empty list', () => {
    expect(findDefaultRepository([])).toBeNull()
    expect(findDefaultRepository(null)).toBeNull()
    expect(findDefaultRepository(undefined)).toBeNull()
  })
})

describe('pickInitialRepositoryName', () => {
  const server: Repository = { name: 'server', gitUrl: 'g', baseBranch: 'main', isDefault: true }
  const web: Repository = { name: 'web', gitUrl: 'g', baseBranch: 'main', isDefault: false }

  it('keeps the current value when it is still declared', () => {
    expect(pickInitialRepositoryName([server, web], 'web')).toBe('web')
  })

  it('switches to the declared default when the current value is unknown', () => {
    expect(pickInitialRepositoryName([server, web], 'archive')).toBe('server')
  })

  it('uses the single declared repository when only one is available', () => {
    expect(pickInitialRepositoryName([server], null)).toBe('server')
    expect(pickInitialRepositoryName([server], 'whatever')).toBe('server')
  })

  it('returns null when the repository list is empty', () => {
    expect(pickInitialRepositoryName([], null)).toBeNull()
    expect(pickInitialRepositoryName(undefined, 'web')).toBeNull()
  })
})

describe('mapCreateIssueError', () => {
  it('flags repository_not_found as an assignment error', () => {
    const mapped = mapCreateIssueError(new ApiError('not declared', 400, null, 'repository_not_found'))
    expect(mapped.isAssignment).toBe(true)
    expect(mapped.code).toBe('repository_not_found')
    expect(mapped.message).toMatch(/repository/i)
  })

  it('flags parent_not_found as an assignment error with a parent-specific message', () => {
    const mapped = mapCreateIssueError(new ApiError('#99 not found', 400, null, 'parent_not_found'))
    expect(mapped.isAssignment).toBe(true)
    expect(mapped.code).toBe('parent_not_found')
    expect(mapped.message).toMatch(/parent/i)
  })

  it('flags parent_ineligible as an assignment error', () => {
    const mapped = mapCreateIssueError(new ApiError('parent done', 409, null, 'parent_ineligible'))
    expect(mapped.isAssignment).toBe(true)
    expect(mapped.code).toBe('parent_ineligible')
    expect(mapped.message).toMatch(/eligible/i)
  })

  it('flags parent_is_sub_issue as an assignment error', () => {
    const mapped = mapCreateIssueError(new ApiError('parent is a child', 409, null, 'parent_is_sub_issue'))
    expect(mapped.isAssignment).toBe(true)
    expect(mapped.code).toBe('parent_is_sub_issue')
    expect(mapped.message).toMatch(/sub-issue/i)
  })

  it('preserves the server-supplied message inside the assignment error', () => {
    const mapped = mapCreateIssueError(new ApiError('Repository \'gone\' is not declared', 400, null, 'repository_not_found'))
    expect(mapped.message).toMatch(/gone/)
  })

  it('returns a non-assignment marker for unrelated errors', () => {
    const mapped = mapCreateIssueError(new ApiError('Boom', 500))
    expect(mapped.isAssignment).toBe(false)
    expect(mapped.code).toBeNull()
    expect(mapped.message).toBe('Boom')
  })

  it('falls back to a generic message when the error is not an ApiError', () => {
    expect(mapCreateIssueError(new Error('Network down')).message).toBe('Network down')
    expect(mapCreateIssueError('raw failure').message).toBe('Failed to create issue')
  })
})
