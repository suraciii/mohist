import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus, type Issue } from '../../src/types';
import type { CheckContext, CheckResult } from '../../src/workflow/checks';
import type { StageContext } from '../../src/workflow/stage-context';
import { EventBus } from '../../src/services/event-bus';
import type { ChangeArtifactsManager, CheckpointManager } from '../../src/workflow/stage-context';
import type { IssueRepo } from '../../src/workflow/stage-context';
import type { ProjectRepo } from '../../src/workflow/stage-context';
import type { WorktreeManager } from '../../src/workflow/stage-context';
import { UserApprovalCheck } from '../../src/workflow/checks/user-approval-check';
import { PlanStageRunner } from '../../src/workflow/plan-stage-runner';
import { BaseStageRunner } from '../../src/workflow/base-stage-runner';
import type { Check } from '../../src/workflow/checks';

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    body: 'Test',
    stage: Stage.Check,
    status: IssueStatus.Active,
    projectId: 'proj-1',
    labels: [],
    priority: 'p2',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    ...overrides,
  };
}

function makeContext(overrides?: Partial<StageContext>): StageContext {
  return {
    issue: makeIssue({ stage: Stage.Check }),
    acpOptions: {} as any,
    artifactManager: {
      getChangeDir: vi.fn().mockReturnValue('/tmp/change'),
      createChangeDir: vi.fn(),
      readArtifact: vi.fn(),
      writeArtifact: vi.fn(),
      exists: vi.fn(),
      readTasks: vi.fn(),
      updateTaskPasses: vi.fn(),
      archiveChange: vi.fn(),
    } as unknown as ChangeArtifactsManager,
    worktreeManager: {} as WorktreeManager,
    projectRepo: {} as ProjectRepo,
    eventBus: new EventBus() as any,
    checkpointManager: {
      save: vi.fn(),
      load: vi.fn(),
      deleteAll: vi.fn(),
    } as unknown as CheckpointManager,
    issueRepo: {
      updateStage: vi.fn(),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
    } as unknown as IssueRepo,
    ...overrides,
  } as StageContext;
}

describe('UserApprovalCheck stage-awareness', () => {
  describe('stale approval is ignored', () => {
    it('Plan-stage approved approvalState does not make Check-stage UserApprovalCheck pass', async () => {
      const check = new UserApprovalCheck(Stage.Check);
      const issue = makeIssue({
        stage: Stage.Check,
        approvalState: {
          stage: Stage.Plan,
          status: 'approved',
          output: null,
          requestedAt: '2024-01-01T00:00:00Z',
        },
      });

      const result = await check.run({ issue } as CheckContext);

      expect(result.status).toBe('pending');
      expect(result.message).toBe('Waiting for user approval');
    });

    it('stale rejected approval does not fail Check-stage UserApprovalCheck', async () => {
      const check = new UserApprovalCheck(Stage.Check);
      const issue = makeIssue({
        stage: Stage.Check,
        approvalState: {
          stage: Stage.Plan,
          status: 'rejected',
          output: null,
          requestedAt: '2024-01-01T00:00:00Z',
        },
      });

      const result = await check.run({ issue } as CheckContext);

      expect(result.status).toBe('pending');
    });

    it('stale awaiting approval does not make Check-stage UserApprovalCheck pending for the wrong stage', async () => {
      const check = new UserApprovalCheck(Stage.Check);
      const issue = makeIssue({
        stage: Stage.Check,
        approvalState: {
          stage: Stage.Plan,
          status: 'awaiting',
          output: null,
          requestedAt: '2024-01-01T00:00:00Z',
        },
      });

      const result = await check.run({ issue } as CheckContext);

      expect(result.status).toBe('pending');
    });

    it('current-stage approved approval passes Check-stage UserApprovalCheck', async () => {
      const check = new UserApprovalCheck(Stage.Check);
      const issue = makeIssue({
        stage: Stage.Check,
        approvalState: {
          stage: Stage.Check,
          status: 'approved',
          output: null,
          requestedAt: '2024-01-01T00:00:00Z',
        },
      });

      const result = await check.run({ issue } as CheckContext);

      expect(result.status).toBe('pass');
    });

    it('current-stage rejected approval fails Check-stage UserApprovalCheck', async () => {
      const check = new UserApprovalCheck(Stage.Check);
      const issue = makeIssue({
        stage: Stage.Check,
        approvalState: {
          stage: Stage.Check,
          status: 'rejected',
          output: null,
          requestedAt: '2024-01-01T00:00:00Z',
        },
      });

      const result = await check.run({ issue } as CheckContext);

      expect(result.status).toBe('fail');
    });

    it('current-stage awaiting approval makes Check-stage UserApprovalCheck pending', async () => {
      const check = new UserApprovalCheck(Stage.Check);
      const issue = makeIssue({
        stage: Stage.Check,
        approvalState: {
          stage: Stage.Check,
          status: 'awaiting',
          output: null,
          requestedAt: '2024-01-01T00:00:00Z',
        },
      });

      const result = await check.run({ issue } as CheckContext);

      expect(result.status).toBe('pending');
      expect(result.message).toBe('Waiting for user approval');
    });
  });
});

describe('UserApprovalCheck stage-aware approval', () => {
  it('UserApprovalCheck is constructed with Stage.Check for check stage', () => {
    const check = new UserApprovalCheck(Stage.Check);
    expect(check.name).toBe('user-approval');
  });

  it('UserApprovalCheck for Plan stage uses Stage.Plan', () => {
    const check = new UserApprovalCheck(Stage.Plan);
    expect(check.name).toBe('user-approval');
  });
});

describe('API: stale awaiting approval not approvable', () => {
  it('reject API rejects only current-stage awaiting approval', async () => {
    const { isCurrentStageApproval } = await import('../../src/workflow/issue-lifecycle');

    const issue = makeIssue({
      stage: Stage.Check,
      approvalState: {
        stage: Stage.Plan,
        status: 'awaiting',
        output: null,
        requestedAt: '2024-01-01T00:00:00Z',
      },
    });

    expect(isCurrentStageApproval(issue, issue.stage, 'awaiting')).toBe(false);
  });

  it('approve API approves only current-stage awaiting approval', async () => {
    const { isCurrentStageApproval } = await import('../../src/workflow/issue-lifecycle');

    const issue = makeIssue({
      stage: Stage.Check,
      approvalState: {
        stage: Stage.Plan,
        status: 'awaiting',
        output: null,
        requestedAt: '2024-01-01T00:00:00Z',
      },
    });

    expect(isCurrentStageApproval(issue, issue.stage, 'awaiting')).toBe(false);
  });

  it('current-stage awaiting approval is correctly identified', async () => {
    const { isCurrentStageApproval } = await import('../../src/workflow/issue-lifecycle');

    const issue = makeIssue({
      stage: Stage.Check,
      approvalState: {
        stage: Stage.Check,
        status: 'awaiting',
        output: null,
        requestedAt: '2024-01-01T00:00:00Z',
      },
    });

    expect(isCurrentStageApproval(issue, issue.stage, 'awaiting')).toBe(true);
  });
});
