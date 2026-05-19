import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus, MergeState, type Issue } from '../src/types';
import { classifyMergeDelivery } from '../src/workflow/issue-lifecycle';
import type { IssueRepo } from '../src/workflow/stage-context';
import type { StageContext } from '../src/workflow/stage-context';
import type { ChangeArtifactsManager, CheckpointManager } from '../src/workflow/stage-context';
import type { ProjectRepo } from '../src/workflow/stage-context';
import type { WorktreeManager } from '../src/workflow/stage-context';
import type { StageRunner } from '../src/workflow/stage-runner';
import type { StageRunResult } from '../src/workflow/stage-context';
import { EventBus } from '../src/services/event-bus';
import { WorkflowEngine } from '../src/workflow/workflow-engine';

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

function makeMockIssueRepo(initialIssue: Issue): IssueRepo {
  let currentIssue = initialIssue;
  return {
    updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) => {
      currentIssue = { ...currentIssue, stage };
      return currentIssue;
    }),
    findById: vi.fn().mockReturnValue(currentIssue),
    setApprovalState: vi.fn(),
    clearApprovalState: vi.fn(),
    updateStatus: vi.fn().mockImplementation((_id: string, status: IssueStatus) => {
      currentIssue = { ...currentIssue, status };
      return currentIssue;
    }),
    updateBlockedReason: vi.fn(),
    setMergeState: vi.fn().mockImplementation((_id: string, ms: MergeState) => {
      currentIssue = { ...currentIssue, mergeState: ms };
      return currentIssue;
    }),
  } as unknown as IssueRepo;
}

function makeMinimalContext(issue: Issue): StageContext {
  return {
    issue,
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
    issueRepo: makeMockIssueRepo(issue),
  } as StageContext;
}

describe('T-007 Regression: approval lifecycle + merge-gated completion', () => {

  describe('AC-1: stale Plan approval at Check requests fresh approval', () => {
    it('Check stage with stale Plan approval does not pass UserApprovalCheck', async () => {
      const { UserApprovalCheck } = await import('../src/workflow/checks/user-approval-check');
      const { CheckContext } = await import('../src/workflow/checks');

      const issue = makeIssue({
        stage: Stage.Check,
        approvalState: {
          stage: Stage.Plan,
          status: 'approved',
          output: null,
          requestedAt: '2024-01-01T00:00:00Z',
        },
      });

      const check = new UserApprovalCheck(Stage.Check);
      const result = await check.run({ issue } as CheckContext);

      expect(result.status).toBe('pending');
      expect(result.message).toBe('Waiting for user approval');
    });

    it('Check stage with stale awaiting Plan approval does not appear as approvable', async () => {
      const { isCurrentStageApproval } = await import('../src/workflow/issue-lifecycle');

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

    it('Check stage with current Check approval passes UserApprovalCheck', async () => {
      const { UserApprovalCheck } = await import('../src/workflow/checks/user-approval-check');
      const { CheckContext } = await import('../src/workflow/checks');

      const issue = makeIssue({
        stage: Stage.Check,
        approvalState: {
          stage: Stage.Check,
          status: 'approved',
          output: null,
          requestedAt: '2024-01-01T00:00:00Z',
        },
      });

      const check = new UserApprovalCheck(Stage.Check);
      const result = await check.run({ issue } as CheckContext);

      expect(result.status).toBe('pass');
    });
  });

  describe('AC-2: Check approval transitions to Integrate — workflow blocks direct Done transition', () => {
    it('Check runner returning nextStage=Integrate is allowed, returning nextStage=Done is blocked by WorkflowEngine', async () => {
      const checkRunner = new class implements StageRunner {
        canHandle(s: Stage): boolean { return s === Stage.Check; }
        async run(): Promise<StageRunResult> {
          return { success: true, nextStage: Stage.Integrate, checkResults: [], output: {} };
        }
      }();

      const integrateRunner = new class implements StageRunner {
        canHandle(s: Stage): boolean { return s === Stage.Integrate; }
        async run(): Promise<StageRunResult> {
          return { success: true, nextStage: Stage.Done, checkResults: [], output: {} };
        }
      }();

      const issue = makeIssue({ stage: Stage.Check });
      const mockRepo = makeMockIssueRepo(issue);

      const engine = new WorkflowEngine({
        runners: [checkRunner, integrateRunner],
        issueRepo: mockRepo,
        eventBus: new EventBus(),
        checkpointManager: {
          save: vi.fn(),
          load: vi.fn(),
          deleteAll: vi.fn(),
        } as unknown as CheckpointManager,
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
      });

      const result = await engine.run(issue, { cwd: '/tmp' });

      expect(result.completed).toBe(false);
      expect(result.stage).toBe(Stage.Check);
      expect(result.message).toContain('aggregate workflow service is unavailable');
    });

    it('Check runner returning success=false leaves issue in Check pending approval', async () => {
      const checkRunner = new class implements StageRunner {
        canHandle(s: Stage): boolean { return s === Stage.Check; }
        async run(): Promise<StageRunResult> {
          return { success: false, checkResults: [], message: 'Waiting for user approval' };
        }
      }();

      const issue = makeIssue({ stage: Stage.Check });
      const mockRepo = makeMockIssueRepo(issue);

      const engine = new WorkflowEngine({
        runners: [checkRunner],
        issueRepo: mockRepo,
        eventBus: new EventBus(),
        checkpointManager: {
          save: vi.fn(),
          load: vi.fn(),
          deleteAll: vi.fn(),
        } as unknown as CheckpointManager,
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
      });

      const result = await engine.run(issue, { cwd: '/tmp' });

      expect(result.completed).toBe(false);
    });
  });

  describe('AC-3: done/completed + mergeState null is classified as done-not-merged', () => {
    function expectAnomaly(issue: Issue) {
      const status = classifyMergeDelivery(issue);
      expect(status).toBe('done-not-merged');
    }

    it('stage=done + status=completed + mergeState=null is done-not-merged', () => {
      expectAnomaly(makeIssue({
        stage: Stage.Done,
        status: IssueStatus.Completed,
        mergeState: null,
      }));
    });

    it('stage=done + status=completed + mergeState=Conflict is done-not-merged', () => {
      expectAnomaly(makeIssue({
        stage: Stage.Done,
        status: IssueStatus.Completed,
        mergeState: MergeState.Conflict,
      }));
    });

    it('stage=done + status=completed + mergeState=Blocked is done-not-merged', () => {
      expectAnomaly(makeIssue({
        stage: Stage.Done,
        status: IssueStatus.Completed,
        mergeState: MergeState.Blocked,
      }));
    });

    it('stage=done + status=completed + mergeState=Pending (not yet merged) is done-not-merged', () => {
      expectAnomaly(makeIssue({
        stage: Stage.Done,
        status: IssueStatus.Completed,
        mergeState: MergeState.Pending,
      }));
    });

    it('stage=check + status=completed + mergeState=null is done-not-merged', () => {
      expectAnomaly(makeIssue({
        stage: Stage.Check,
        status: IssueStatus.Completed,
        mergeState: null,
      }));
    });

    it('stage=done + status=completed + mergeState=Merged is NOT done-not-merged', () => {
      const issue = makeIssue({
        stage: Stage.Done,
        status: IssueStatus.Completed,
        mergeState: MergeState.Merged,
      });
      expect(classifyMergeDelivery(issue)).toBe('merged');
    });

    it('stage=check + mergeState=merged (async) is merged (not done-not-merged)', () => {
      const issue = makeIssue({
        stage: Stage.Check,
        status: IssueStatus.Active,
        mergeState: MergeState.Merged,
      });
      expect(classifyMergeDelivery(issue)).toBe('merged');
    });
  });

  describe('AC-4: archive-all-completed skips false-done issues', () => {
    it('archiveAllCompleted skips stage=done + mergeState=null', async () => {
      const { DatabaseManager } = await import('../src/db/database');
      const { initializeDatabase } = await import('../src/db/migrations');
      const { IssueRepo } = await import('../src/db/issue-repo');
      const { ProjectRepo } = await import('../src/db/project-repo');
      const { IssueService } = await import('../src/services/issue-service');
      const { CommentRepo } = await import('../src/db/comment-repo');

      const db = new DatabaseManager({ inMemory: true });
      initializeDatabase(db);
      try {
        const projectRepo = new ProjectRepo(db);
        const project = projectRepo.create({ name: 'Test', path: '/test' });
        const issueRepo = new IssueRepo(db);
        const commentRepo = new CommentRepo(db);
        const service = new IssueService(issueRepo, commentRepo, projectRepo);

        const falseDone = issueRepo.create({ number: 1, projectId: project.id, title: 'False Done' });
        issueRepo.updateStage(falseDone.id, Stage.Done);
        issueRepo.updateStatus(falseDone.id, IssueStatus.Completed);

        const trulyDone = issueRepo.create({ number: 2, projectId: project.id, title: 'Truly Done' });
        issueRepo.updateStage(trulyDone.id, Stage.Done);
        issueRepo.setMergeState(trulyDone.id, MergeState.Merged);

        const result = await service.archiveAllCompleted(project.id);

        expect(result.count).toBe(1);
        expect(result.skipped).toBe(1);
        expect(result.skippedNumbers).toContain(1);
        expect(result.message).toContain('false-done');
      } finally {
        db.close();
      }
    });

    it('archiveAllCompleted skips stage=done + mergeState=Conflict', async () => {
      const { DatabaseManager } = await import('../src/db/database');
      const { initializeDatabase } = await import('../src/db/migrations');
      const { IssueRepo } = await import('../src/db/issue-repo');
      const { ProjectRepo } = await import('../src/db/project-repo');
      const { IssueService } = await import('../src/services/issue-service');
      const { CommentRepo } = await import('../src/db/comment-repo');

      const db = new DatabaseManager({ inMemory: true });
      initializeDatabase(db);
      try {
        const projectRepo = new ProjectRepo(db);
        const project = projectRepo.create({ name: 'Test', path: '/test' });
        const issueRepo = new IssueRepo(db);
        const commentRepo = new CommentRepo(db);
        const service = new IssueService(issueRepo, commentRepo, projectRepo);

        const falseDone = issueRepo.create({ number: 1, projectId: project.id, title: 'False Done Conflict' });
        issueRepo.updateStage(falseDone.id, Stage.Done);
        issueRepo.updateStatus(falseDone.id, IssueStatus.Completed);
        issueRepo.setMergeState(falseDone.id, MergeState.Conflict);

        const trulyDone = issueRepo.create({ number: 2, projectId: project.id, title: 'Truly Done' });
        issueRepo.updateStage(trulyDone.id, Stage.Done);
        issueRepo.setMergeState(trulyDone.id, MergeState.Merged);

        const result = await service.archiveAllCompleted(project.id);

        expect(result.count).toBe(1);
        expect(result.skipped).toBe(1);
        expect(result.skippedNumbers).toContain(1);
      } finally {
        db.close();
      }
    });
  });

  describe('AC-5: mergeState null state matrix — classifyMergeDelivery', () => {
    it('null mergeState in backlog/plan/build/check returns not-ready', () => {
      for (const stage of [Stage.Backlog, Stage.Plan, Stage.Build, Stage.Check] as Stage[]) {
        const issue = makeIssue({ stage, mergeState: null });
        expect(classifyMergeDelivery(issue)).toBe('not-ready');
      }
    });

    it('null mergeState in integrate returns integrating', () => {
      const issue = makeIssue({ stage: Stage.Integrate, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('integrating');
    });

    it('null mergeState in backlog returns not-ready', () => {
      const issue = makeIssue({ stage: Stage.Backlog, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('not-ready');
    });

    it('done/completed + null mergeState returns done-not-merged', () => {
      const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Completed, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('done-not-merged');
    });

    it('completed status (no stage=done) + null mergeState returns done-not-merged', () => {
      const issue = makeIssue({ stage: Stage.Check, status: IssueStatus.Completed, mergeState: null });
      expect(classifyMergeDelivery(issue)).toBe('done-not-merged');
    });

    it('active merge states are correctly classified', () => {
      const cases: Array<[MergeState, string]> = [
        [MergeState.Merged, 'merged'],
        [MergeState.Pending, 'queued'],
        [MergeState.Rebasing, 'rebasing'],
        [MergeState.Merging, 'merging'],
        [MergeState.Resolving, 'resolving'],
        [MergeState.Conflict, 'conflict'],
        [MergeState.BuildFailed, 'build-failed'],
        [MergeState.Blocked, 'blocked'],
      ];
      for (const [ms, expected] of cases) {
        const issue = makeIssue({ stage: Stage.Check, mergeState: ms });
        expect(classifyMergeDelivery(issue)).toBe(expected);
      }
    });

    it('done stage + merged mergeState returns merged (trusted completion)', () => {
      const issue = makeIssue({ stage: Stage.Done, status: IssueStatus.Completed, mergeState: MergeState.Merged });
      expect(classifyMergeDelivery(issue)).toBe('merged');
    });
  });

  describe('AC-6: stale Plan approval leaked into Check — integration scenario', () => {
    it('workflow with Plan-approved + Check runner blocks completion despite plan approval', async () => {
      const planRunner = new class implements StageRunner {
        canHandle(s: Stage): boolean { return s === Stage.Plan; }
        async run(): Promise<StageRunResult> {
          return { success: true, nextStage: Stage.Build, checkResults: [], output: {} };
        }
      }();

      const buildRunner = new class implements StageRunner {
        canHandle(s: Stage): boolean { return s === Stage.Build; }
        async run(): Promise<StageRunResult> {
          return { success: true, nextStage: Stage.Check, checkResults: [], output: {} };
        }
      }();

      const checkRunner = new class implements StageRunner {
        canHandle(s: Stage): boolean { return s === Stage.Check; }
        async run(): Promise<StageRunResult> {
          return { success: true, nextStage: Stage.Integrate, checkResults: [], output: {} };
        }
      }();

      const integrateRunner = new class implements StageRunner {
        canHandle(s: Stage): boolean { return s === Stage.Integrate; }
        async run(): Promise<StageRunResult> {
          return { success: true, nextStage: Stage.Done, checkResults: [], output: {} };
        }
      }();

      const issue = makeIssue({
        stage: Stage.Plan,
        approvalState: {
          stage: Stage.Plan,
          status: 'approved',
          output: null,
          requestedAt: '2024-01-01T00:00:00Z',
        },
      });
      const mockRepo = makeMockIssueRepo(issue);

      const engine = new WorkflowEngine({
        runners: [planRunner, buildRunner, checkRunner, integrateRunner],
        issueRepo: mockRepo,
        eventBus: new EventBus(),
        checkpointManager: {
          save: vi.fn(),
          load: vi.fn(),
          deleteAll: vi.fn(),
        } as unknown as CheckpointManager,
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
      });

      const result = await engine.run(issue, { cwd: '/tmp' });

      expect(result.completed).toBe(false);
      expect(result.stage).toBe(Stage.Plan);
      expect(result.message).toContain('aggregate workflow service is unavailable');
    });
  });
});
