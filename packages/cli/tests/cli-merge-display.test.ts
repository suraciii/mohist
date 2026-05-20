import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus, MergeState, type Issue } from '../src/types';
import { classifyMergeDelivery, type MergeDeliveryStatus } from '../src/workflow/issue-lifecycle';

describe('CLI merge display formatting', () => {
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

  describe('classifyMergeDelivery for CLI output', () => {
    it('returns merged for merged state', () => {
      const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Completed, mergeState: MergeState.Merged });
      expect(classifyMergeDelivery(issue)).toBe('merged');
    });

    it('returns queued for pending state', () => {
      const issue = makeIssue({ mergeState: MergeState.Pending });
      expect(classifyMergeDelivery(issue)).toBe('queued');
    });

    it('returns rebasing for rebasing state', () => {
      const issue = makeIssue({ mergeState: MergeState.Rebasing });
      expect(classifyMergeDelivery(issue)).toBe('rebasing');
    });

    it('returns merging for merging state', () => {
      const issue = makeIssue({ mergeState: MergeState.Merging });
      expect(classifyMergeDelivery(issue)).toBe('merging');
    });

    it('returns resolving for resolving state', () => {
      const issue = makeIssue({ mergeState: MergeState.Resolving });
      expect(classifyMergeDelivery(issue)).toBe('resolving');
    });

    it('returns conflict for conflict state', () => {
      const issue = makeIssue({ mergeState: MergeState.Conflict });
      expect(classifyMergeDelivery(issue)).toBe('conflict');
    });

    it('returns build-failed for build-failed state', () => {
      const issue = makeIssue({ mergeState: MergeState.BuildFailed });
      expect(classifyMergeDelivery(issue)).toBe('build-failed');
    });

    it('returns blocked for blocked state', () => {
      const issue = makeIssue({ mergeState: MergeState.Blocked });
      expect(classifyMergeDelivery(issue)).toBe('blocked');
    });

    it('returns not-merged when stage=done but mergeState not merged', () => {
      const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Completed, mergeState: MergeState.Conflict });
      expect(classifyMergeDelivery(issue)).toBe('not-merged');
    });

    it('returns not-ready for backlog + null mergeState', () => {
      const issue = makeIssue({ stage: Stage.Backlog, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-ready');
    });

    it('returns not-ready for plan + null mergeState', () => {
      const issue = makeIssue({ stage: Stage.Plan, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-ready');
    });

    it('returns not-ready for build + null mergeState', () => {
      const issue = makeIssue({ stage: Stage.Build, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-ready');
    });

    it('returns not-ready for check + null mergeState', () => {
      const issue = makeIssue({ stage: Stage.Check, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-ready');
    });

    it('returns not-merged for done + null mergeState', () => {
      const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Completed, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-merged');
    });

    it('returns integrating for integrate + null mergeState', () => {
      const issue = makeIssue({ stage: Stage.Integrate, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('integrating');
    });

    it('returns not-merged for completed status + null mergeState', () => {
      const issue = makeIssue({ status: IssueStatus.Completed, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-merged');
    });

    it('returns merged even if stage not done yet', () => {
      const issue = makeIssue({ stage: Stage.Check, mergeState: MergeState.Merged });
      expect(classifyMergeDelivery(issue)).toBe('merged');
    });
  });

  describe('not-merged detection', () => {
    it('detects not-merged: stage=done + null mergeState', () => {
      const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Completed, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-merged');
    });

    it('detects not-merged: stage=done + conflict mergeState', () => {
      const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Completed, mergeState: MergeState.Conflict });
      expect(classifyMergeDelivery(issue)).toBe('not-merged');
    });

    it('detects not-merged: stage=done + blocked mergeState', () => {
      const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Completed, mergeState: MergeState.Blocked });
      expect(classifyMergeDelivery(issue)).toBe('not-merged');
    });

    it('detects not-merged: stage=done + build-failed mergeState', () => {
      const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Completed, mergeState: MergeState.BuildFailed });
      expect(classifyMergeDelivery(issue)).toBe('not-merged');
    });

    it('detects not-merged: status=completed + null mergeState even without stage=done', () => {
      const issue = makeIssue({ status: IssueStatus.Completed, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-merged');
    });

    it('does not flag not-merged when mergeState=merged', () => {
      const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Completed, mergeState: MergeState.Merged });
      expect(classifyMergeDelivery(issue)).toBe('merged');
    });

    it('does not flag not-merged when stage not done', () => {
      const issue = makeIssue({ stage: Stage.Check, status: IssueStatus.Active, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-ready');
    });
  });
});
