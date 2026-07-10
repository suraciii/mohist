import { describe, expect, it } from 'vitest'
import { buildDriftRecoveryAction } from './buildDriftRecoveryAction'
import type { RebaseRecovery } from '../../../widgets/issue-workflow'

function makeRebase(overrides: Partial<RebaseRecovery> = {}): RebaseRecovery {
  return {
    trigger: () => {},
    isPending: false,
    isQueued: false,
    isRebasing: false,
    isConflictResolving: false,
    isConflictFailed: false,
    canRequest: true,
    hasConflicts: null,
    error: null,
    rebaseConflict: null,
    workspace: {
      data: { exists: true, branch: 'mohist/run-wr-14', baseBranch: 'master', ahead: 0, behind: 5 },
      isLoading: false,
      isChecking: false,
      hasAheadBehind: true,
      isUpstreamUnknown: false,
      isBehind: true,
      ahead: 0,
      behind: 5,
      branch: 'mohist/run-wr-14',
      baseBranch: 'master',
    },
    ...overrides,
  }
}

describe('buildDriftRecoveryAction', () => {
  it('returns null when there is no drift', () => {
    expect(buildDriftRecoveryAction({
      drift: null,
      rebase: makeRebase(),
    })).toBeNull()
  })

  it('returns null when drift.drifted is false', () => {
    expect(buildDriftRecoveryAction({
      drift: { drifted: false, decision: 'needs-attention' },
      rebase: makeRebase(),
    })).toBeNull()
  })

  it('returns null when drift.decision is "defer"', () => {
    expect(buildDriftRecoveryAction({
      drift: { drifted: true, decision: 'defer' },
      rebase: makeRebase(),
    })).toBeNull()
  })

  it('returns null when drift.decision is "suggest"', () => {
    expect(buildDriftRecoveryAction({
      drift: { drifted: true, decision: 'suggest' },
      rebase: makeRebase(),
    })).toBeNull()
  })

  it('returns null when drift.decision is "enqueue"', () => {
    expect(buildDriftRecoveryAction({
      drift: { drifted: true, decision: 'enqueue' },
      rebase: makeRebase(),
    })).toBeNull()
  })

  it('returns a DriftRecoveryAction when drift.drifted is true and decision is needs-attention', () => {
    const action = buildDriftRecoveryAction({
      drift: { drifted: true, decision: 'needs-attention' },
      rebase: makeRebase(),
    })
    expect(action).not.toBeNull()
    expect(action?.baseBranch).toBe('master')
    expect(action?.canRequest).toBe(true)
  })

  it('forwards the workspace baseBranch to the action', () => {
    const action = buildDriftRecoveryAction({
      drift: { drifted: true, decision: 'needs-attention' },
      rebase: makeRebase({
        workspace: {
          data: { exists: true, branch: 'mohist/run-wr-14', baseBranch: 'develop', ahead: 0, behind: 5 },
          isLoading: false,
          isChecking: false,
          hasAheadBehind: true,
          isUpstreamUnknown: false,
          isBehind: true,
          ahead: 0,
          behind: 5,
          branch: 'mohist/run-wr-14',
          baseBranch: 'develop',
        },
      }),
      baseBranchFallback: 'main',
    })
    expect(action?.baseBranch).toBe('develop')
  })

  it('forwards rebase state (canRequest) from the rebase hook', () => {
    const action = buildDriftRecoveryAction({
      drift: { drifted: true, decision: 'needs-attention' },
      rebase: makeRebase({ canRequest: false, isPending: true }),
    })
    expect(action?.canRequest).toBe(false)
    expect(action?.isPending).toBe(true)
  })

  it('forwards conflict state (isConflictFailed, hasConflicts) from the rebase hook', () => {
    const action = buildDriftRecoveryAction({
      drift: { drifted: true, decision: 'needs-attention' },
      rebase: makeRebase({
        isConflictFailed: true,
        hasConflicts: ['packages/server/src/foo.ts'],
      }),
    })
    expect(action?.isConflictFailed).toBe(true)
    expect(action?.hasConflicts).toEqual(['packages/server/src/foo.ts'])
  })
})