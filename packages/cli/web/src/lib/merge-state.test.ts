import { describe, it, expect } from 'vitest'
import { Stage, IssueStatus } from './types'

export type MergeDeliveryStatus =
  | 'merged'
  | 'queued'
  | 'rebasing'
  | 'merging'
  | 'resolving'
  | 'conflict'
  | 'build-failed'
  | 'blocked'
  | 'not-ready'
  | 'not-merged'
  | 'unknown'
  | 'done-not-merged'

function classifyMergeDelivery(issue: { stage: string; status: string; mergeState?: string | null }): MergeDeliveryStatus {
  const { stage, status, mergeState } = issue

  if (stage === 'done' || status === 'completed') {
    if (mergeState === 'merged') {
      return 'merged'
    }
    return 'done-not-merged'
  }

  if (mergeState === null || mergeState === undefined) {
    if (stage === 'draft' || stage === 'plan' || stage === 'build' || stage === 'check') {
      return 'not-ready'
    }
    return 'unknown'
  }

  switch (mergeState) {
    case 'merged':
      return 'merged'
    case 'pending':
      return 'queued'
    case 'rebasing':
      return 'rebasing'
    case 'merging':
      return 'merging'
    case 'resolving':
      return 'resolving'
    case 'conflict':
      return 'conflict'
    case 'build-failed':
      return 'build-failed'
    case 'blocked':
      return 'blocked'
    default:
      return 'unknown'
  }
}

describe('classifyMergeDelivery', () => {
  describe('null mergeState handling', () => {
    it('returns not-ready for draft stage with null mergeState', () => {
      const issue = { stage: Stage.Draft, status: IssueStatus.Active, mergeState: null }
      expect(classifyMergeDelivery(issue)).toBe('not-ready')
    })

    it('returns not-ready for plan stage with null mergeState', () => {
      const issue = { stage: Stage.Plan, status: IssueStatus.Active, mergeState: null }
      expect(classifyMergeDelivery(issue)).toBe('not-ready')
    })

    it('returns not-ready for build stage with null mergeState', () => {
      const issue = { stage: Stage.Build, status: IssueStatus.Active, mergeState: null }
      expect(classifyMergeDelivery(issue)).toBe('not-ready')
    })

    it('returns not-ready for check stage with null mergeState', () => {
      const issue = { stage: Stage.Check, status: IssueStatus.Active, mergeState: null }
      expect(classifyMergeDelivery(issue)).toBe('not-ready')
    })

    it('returns unknown for backlog stage with null mergeState', () => {
      const issue = { stage: Stage.Backlog, status: IssueStatus.Active, mergeState: null }
      expect(classifyMergeDelivery(issue)).toBe('unknown')
    })

    it('returns not-ready for undefined mergeState in plan stage', () => {
      const issue = { stage: Stage.Plan, status: IssueStatus.Active }
      expect(classifyMergeDelivery(issue)).toBe('not-ready')
    })
  })

  describe('done/completed anomaly detection', () => {
    it('returns merged for done stage with mergeState=merged', () => {
      const issue = { stage: Stage.Done, status: IssueStatus.Completed, mergeState: 'merged' }
      expect(classifyMergeDelivery(issue)).toBe('merged')
    })

    it('returns done-not-merged for done stage with null mergeState', () => {
      const issue = { stage: Stage.Done, status: IssueStatus.Completed, mergeState: null }
      expect(classifyMergeDelivery(issue)).toBe('done-not-merged')
    })

    it('returns done-not-merged for done stage with undefined mergeState', () => {
      const issue = { stage: Stage.Done, status: IssueStatus.Completed }
      expect(classifyMergeDelivery(issue)).toBe('done-not-merged')
    })

    it('returns done-not-merged for done stage with mergeState=pending', () => {
      const issue = { stage: Stage.Done, status: IssueStatus.Completed, mergeState: 'pending' }
      expect(classifyMergeDelivery(issue)).toBe('done-not-merged')
    })

    it('returns done-not-merged for done stage with mergeState=conflict', () => {
      const issue = { stage: Stage.Done, status: IssueStatus.Completed, mergeState: 'conflict' }
      expect(classifyMergeDelivery(issue)).toBe('done-not-merged')
    })

    it('returns done-not-merged for completed status with null mergeState', () => {
      const issue = { stage: Stage.Check, status: IssueStatus.Completed, mergeState: null }
      expect(classifyMergeDelivery(issue)).toBe('done-not-merged')
    })
  })

  describe('active merge states', () => {
    it('returns merged for mergeState=merged', () => {
      const issue = { stage: Stage.Done, status: IssueStatus.Completed, mergeState: 'merged' }
      expect(classifyMergeDelivery(issue)).toBe('merged')
    })

    it('returns queued for mergeState=pending', () => {
      const issue = { stage: Stage.Check, status: IssueStatus.Active, mergeState: 'pending' }
      expect(classifyMergeDelivery(issue)).toBe('queued')
    })

    it('returns rebasing for mergeState=rebasing', () => {
      const issue = { stage: Stage.Check, status: IssueStatus.Active, mergeState: 'rebasing' }
      expect(classifyMergeDelivery(issue)).toBe('rebasing')
    })

    it('returns merging for mergeState=merging', () => {
      const issue = { stage: Stage.Check, status: IssueStatus.Active, mergeState: 'merging' }
      expect(classifyMergeDelivery(issue)).toBe('merging')
    })

    it('returns resolving for mergeState=resolving', () => {
      const issue = { stage: Stage.Check, status: IssueStatus.Active, mergeState: 'resolving' }
      expect(classifyMergeDelivery(issue)).toBe('resolving')
    })

    it('returns conflict for mergeState=conflict', () => {
      const issue = { stage: Stage.Check, status: IssueStatus.Active, mergeState: 'conflict' }
      expect(classifyMergeDelivery(issue)).toBe('conflict')
    })

    it('returns build-failed for mergeState=build-failed', () => {
      const issue = { stage: Stage.Check, status: IssueStatus.Active, mergeState: 'build-failed' }
      expect(classifyMergeDelivery(issue)).toBe('build-failed')
    })

    it('returns blocked for mergeState=blocked', () => {
      const issue = { stage: Stage.Check, status: IssueStatus.Active, mergeState: 'blocked' }
      expect(classifyMergeDelivery(issue)).toBe('blocked')
    })

    it('returns unknown for unknown mergeState value', () => {
      const issue = { stage: Stage.Check, status: IssueStatus.Active, mergeState: 'unknown-value' }
      expect(classifyMergeDelivery(issue)).toBe('unknown')
    })
  })
})