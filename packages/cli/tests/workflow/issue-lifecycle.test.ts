import { describe, it, expect } from 'vitest';
import { Stage, IssueStatus, MergeState, type Issue } from '../../src/types';
import { isCurrentStageApproval, classifyMergeDelivery, type MergeDeliveryStatus } from '../../src/workflow/issue-lifecycle';

describe('isCurrentStageApproval', () => {
  function makeIssue(overrides: Partial<Issue> = {}): Issue {
    return {
      id: 'issue-1',
      number: 1,
      title: 'Test Issue',
      stage: Stage.Check,
      status: IssueStatus.Active,
      projectId: 'proj-1',
      labels: [],
      priority: 'p2',
      createdAt: '2024-01-01T00:00:00Z',
      updatedAt: '2024-01-01T00:00:00Z',
      ...overrides,
    };
  }

  it('returns true when approvalState.stage equals issue stage and no status filter', () => {
    const issue = makeIssue({ stage: Stage.Check, approvalState: { stage: Stage.Check, status: 'awaiting', output: null, requestedAt: '2024-01-01T00:00:00Z' } });
    expect(isCurrentStageApproval(issue)).toBe(true);
  });

  it('returns true when approvalState.stage equals issue stage and status matches', () => {
    const issue = makeIssue({ stage: Stage.Check, approvalState: { stage: Stage.Check, status: 'approved', output: null, requestedAt: '2024-01-01T00:00:00Z' } });
    expect(isCurrentStageApproval(issue, Stage.Check, 'approved')).toBe(true);
  });

  it('returns false when approvalState.stage does not equal issue stage', () => {
    const issue = makeIssue({ stage: Stage.Check, approvalState: { stage: Stage.Plan, status: 'approved', output: null, requestedAt: '2024-01-01T00:00:00Z' } });
    expect(isCurrentStageApproval(issue)).toBe(false);
  });

  it('returns false when approvalState.stage equals explicit stage but status does not match', () => {
    const issue = makeIssue({ stage: Stage.Check, approvalState: { stage: Stage.Check, status: 'rejected', output: null, requestedAt: '2024-01-01T00:00:00Z' } });
    expect(isCurrentStageApproval(issue, Stage.Check, 'approved')).toBe(false);
  });

  it('returns false when stage filter is supplied and does not match', () => {
    const issue = makeIssue({ stage: Stage.Check, approvalState: { stage: Stage.Build, status: 'approved', output: null, requestedAt: '2024-01-01T00:00:00Z' } });
    expect(isCurrentStageApproval(issue, Stage.Check)).toBe(false);
  });

  it('returns false when no approvalState exists', () => {
    const issue = makeIssue({ approvalState: undefined });
    expect(isCurrentStageApproval(issue)).toBe(false);
  });

  it('treats stale awaiting approval as not current-stage approval', () => {
    const issue = makeIssue({ stage: Stage.Check, approvalState: { stage: Stage.Plan, status: 'awaiting', output: null, requestedAt: '2024-01-01T00:00:00Z' } });
    expect(isCurrentStageApproval(issue)).toBe(false);
  });

  it('treats stale approved approval as not current-stage approval', () => {
    const issue = makeIssue({ stage: Stage.Check, approvalState: { stage: Stage.Plan, status: 'approved', output: null, requestedAt: '2024-01-01T00:00:00Z' } });
    expect(isCurrentStageApproval(issue)).toBe(false);
  });

  it('treats stale rejected approval as not current-stage approval', () => {
    const issue = makeIssue({ stage: Stage.Check, approvalState: { stage: Stage.Plan, status: 'rejected', output: null, requestedAt: '2024-01-01T00:00:00Z' } });
    expect(isCurrentStageApproval(issue)).toBe(false);
  });
});

describe('classifyMergeDelivery', () => {
  function makeIssue(overrides: Partial<Issue> = {}): Issue {
    return {
      id: 'issue-1',
      number: 1,
      title: 'Test Issue',
      stage: Stage.Check,
      status: IssueStatus.Active,
      projectId: 'proj-1',
      labels: [],
      priority: 'p2',
      createdAt: '2024-01-01T00:00:00Z',
      updatedAt: '2024-01-01T00:00:00Z',
      ...overrides,
    };
  }

  describe('merged states', () => {
    it('returns merged when stage=done and mergeState=merged', () => {
      const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Completed, mergeState: MergeState.Merged });
      expect(classifyMergeDelivery(issue)).toBe('merged');
    });

    it('returns merged when mergeState=merged even before stage=done', () => {
      const issue = makeIssue({ stage: Stage.Check, mergeState: MergeState.Merged });
      expect(classifyMergeDelivery(issue)).toBe('merged');
    });
  });

  describe('queue/merge states', () => {
    it('returns queued for mergeState=pending', () => {
      const issue = makeIssue({ mergeState: MergeState.Pending });
      expect(classifyMergeDelivery(issue)).toBe('queued');
    });

    it('returns rebasing for mergeState=rebasing', () => {
      const issue = makeIssue({ mergeState: MergeState.Rebasing });
      expect(classifyMergeDelivery(issue)).toBe('rebasing');
    });

    it('returns merging for mergeState=merging', () => {
      const issue = makeIssue({ mergeState: MergeState.Merging });
      expect(classifyMergeDelivery(issue)).toBe('merging');
    });

    it('returns resolving for mergeState=resolving', () => {
      const issue = makeIssue({ mergeState: MergeState.Resolving });
      expect(classifyMergeDelivery(issue)).toBe('resolving');
    });

    it('returns conflict for mergeState=conflict', () => {
      const issue = makeIssue({ mergeState: MergeState.Conflict });
      expect(classifyMergeDelivery(issue)).toBe('conflict');
    });

    it('returns build-failed for mergeState=build-failed', () => {
      const issue = makeIssue({ mergeState: MergeState.BuildFailed });
      expect(classifyMergeDelivery(issue)).toBe('build-failed');
    });

    it('returns blocked for mergeState=blocked', () => {
      const issue = makeIssue({ mergeState: MergeState.Blocked });
      expect(classifyMergeDelivery(issue)).toBe('blocked');
    });
  });

  describe('null mergeState across stages', () => {
    it('returns not-ready for backlog + null', () => {
      const issue = makeIssue({ stage: Stage.Backlog, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-ready');
    });

    it('returns not-ready for plan + null', () => {
      const issue = makeIssue({ stage: Stage.Plan, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-ready');
    });

    it('returns not-ready for build + null', () => {
      const issue = makeIssue({ stage: Stage.Build, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-ready');
    });

    it('returns not-ready for check + null', () => {
      const issue = makeIssue({ stage: Stage.Check, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-ready');
    });


  });

  describe('done/completed + merge state', () => {
    it('returns not-merged for stage=done + null', () => {
      const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Completed, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-merged');
    });

    it('returns not-merged for status=completed + null', () => {
      const issue = makeIssue({ status: IssueStatus.Completed, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-merged');
    });

    it('returns not-merged for stage=done + non-merged mergeState', () => {
      const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Completed, mergeState: MergeState.Conflict });
      expect(classifyMergeDelivery(issue)).toBe('not-merged');
    });
  });

  describe('done/completed + merged is not anomaly', () => {
    it('returns merged for stage=done + status=completed + mergeState=merged', () => {
      const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Completed, mergeState: MergeState.Merged });
      expect(classifyMergeDelivery(issue)).toBe('merged');
    });
  });

  describe('unknown state', () => {
    it('returns not-merged for done + null mergeState', () => {
      const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Active, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-merged');
    });

    it('returns not-ready for backlog stage + null', () => {
      const issue = makeIssue({ stage: Stage.Backlog, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-ready');
    });
  });
});
