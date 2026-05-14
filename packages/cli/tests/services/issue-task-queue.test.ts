import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { DatabaseManager } from '../../src/db/database';
import { initializeDatabase } from '../../src/db/migrations';
import { ProjectRepo } from '../../src/db/project-repo';
import { IssueRepo } from '../../src/db/issue-repo';
import { IssueTaskQueueRepo } from '../../src/db/issue-task-queue-repo';
import { AgentRunnerService } from '../../src/services/agent-runner-service';
import { EventBus } from '../../src/services/event-bus';
import { IssueService } from '../../src/services/issue-service';
import { Stage, IssueStatus, MergeState } from '../../src/types';
import * as fs from 'fs';
import * as path from 'path';

let projectCounter = 0;

function touchFetchCache(projectPath: string) {
  const gitDir = path.join(projectPath, '.git');
  fs.mkdirSync(gitDir, { recursive: true });
  fs.writeFileSync(path.join(gitDir, 'mohist-last-fetch'), Date.now().toString(), 'utf-8');
}

function createHangingWorktreeManager() {
  return {
    exists: vi.fn().mockReturnValue(false),
    create: vi.fn().mockReturnValue(new Promise(() => {})),
    getPath: vi.fn().mockReturnValue('/tmp/worktree/issue-1'),
    remove: vi.fn().mockResolvedValue(undefined),
    canFastForward: vi.fn().mockResolvedValue(true),
    rebaseOntoMaster: vi.fn().mockResolvedValue({ success: true, conflicts: [] }),
    abortRebase: vi.fn().mockResolvedValue(undefined),
  } as any;
}

describe('IssueTaskQueue', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let issueService: IssueService;
  let eventBus: EventBus;
  let taskQueueRepo: IssueTaskQueueRepo;

  beforeEach(() => {
    projectCounter = 0;
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    issueService = new IssueService(issueRepo);
    eventBus = new EventBus();
    taskQueueRepo = new IssueTaskQueueRepo(db);
  });

  afterEach(() => {
    db.close();
  });

  function setupProject(name?: string) {
    const n = name ?? `project-${++projectCounter}`;
    return projectRepo.create({ name: n, path: `/tmp/${n}`, baseBranch: 'main' });
  }

  function setupIssue(projectId: string, title = 'Test Issue') {
    return issueService.create({ projectId, title });
  }

  function createService(maxConcurrent = 8, wtManager?: any) {
    return new AgentRunnerService(
      eventBus, undefined, issueRepo, maxConcurrent,
      undefined, undefined, projectRepo,
      wtManager ?? createHangingWorktreeManager(),
      taskQueueRepo,
    );
  }

  describe('enqueue', () => {
    it('should create task and schedule it to start when no other tasks running', () => {
      const project = setupProject();
      const issue = setupIssue(project.id);

      const service = createService();
      const result = service.enqueue(issue.id, 'start-pipeline');

      expect(result.taskId).toBeTruthy();
      expect(result.status).toBe('running');

      const dbRecord = taskQueueRepo.findById(result.taskId);
      expect(dbRecord).not.toBeNull();
      expect(dbRecord!.status).toBe('running');
      expect(dbRecord!.issueId).toBe(issue.id);
      expect(dbRecord!.taskType).toBe('start-pipeline');
    });

    it('should throw error for non-existent issueId', () => {
      const service = createService();
      expect(() => service.enqueue('non-existent-id', 'start-pipeline')).toThrow(/Issue not found/);
    });

    it('should enqueue as pending while issue has running task', () => {
      const project = setupProject();
      const issue = setupIssue(project.id);

      const service = createService();
      const first = service.enqueue(issue.id, 'start-pipeline');
      const second = service.enqueue(issue.id, 'rebase');

      expect(first.status).toBe('running');
      expect(second.status).toBe('pending');
      expect(second.queuePosition).toBe(0);
    });

    it('should persist task to database on enqueue', () => {
      const project = setupProject();
      const issue = setupIssue(project.id);

      const service = createService();
      const result = service.enqueue(issue.id, 'start-pipeline', { key: 'value' });

      const record = taskQueueRepo.findById(result.taskId);
      expect(record).not.toBeNull();
      expect(record!.payload).toBe('{"key":"value"}');
    });
  });

  describe('priority insertion', () => {
    it('should insert higher priority task ahead of lower priority task', () => {
      const service = createService(1);

      const blockerProject = setupProject();
      const blockerIssue = setupIssue(blockerProject.id);
      service.enqueue(blockerIssue.id, 'start-pipeline');

      const project = setupProject();
      const issue = setupIssue(project.id);

      const low = service.enqueue(issue.id, 'start-pipeline', {}, { priority: 0 });
      const high = service.enqueue(issue.id, 'resume-pipeline', {}, { priority: 10 });

      expect(low.status).toBe('pending');
      expect(high.status).toBe('pending');

      const status = service.getQueueStatus(issue.id) as any;
      const pendingIds = status.pending.map((t: any) => t.id);

      expect(pendingIds.indexOf(high.taskId)).toBeLessThan(pendingIds.indexOf(low.taskId));
      expect(pendingIds.indexOf(high.taskId)).toBe(0);
    });

    it('should maintain FIFO order within same priority', () => {
      const service = createService(1);

      const blockerProject = setupProject();
      const blockerIssue = setupIssue(blockerProject.id);
      service.enqueue(blockerIssue.id, 'start-pipeline');

      const project = setupProject();
      const issue = setupIssue(project.id);

      const taskA = service.enqueue(issue.id, 'start-pipeline', {}, { priority: 0 });
      const taskB = service.enqueue(issue.id, 'rebase', {}, { priority: 0 });

      const status = service.getQueueStatus(issue.id) as any;
      const pendingIds = status.pending.map((t: any) => t.id);

      expect(pendingIds.indexOf(taskA.taskId)).toBeLessThan(pendingIds.indexOf(taskB.taskId));
    });
  });

  describe('global slot limit', () => {
    it('should make tasks wait when all slots are occupied', () => {
      const service = createService(2);

      const results: any[] = [];
      for (let i = 0; i < 4; i++) {
        const project = setupProject();
        const issue = setupIssue(project.id);
        results.push(service.enqueue(issue.id, 'start-pipeline'));
      }

      const runningCount = results.filter(r => r.status === 'running').length;
      const pendingCount = results.filter(r => r.status === 'pending').length;

      expect(runningCount).toBe(2);
      expect(pendingCount).toBe(2);
    });

    it('should report correct maxSlots in global status', () => {
      const service = createService(4);
      const status = service.getQueueStatus() as any;
      expect(status.maxSlots).toBe(4);
    });
  });

  describe('per-issue serialization', () => {
    it('should skip issue with running task when scheduling', () => {
      const service = createService(8);

      const project = setupProject();
      const issue = setupIssue(project.id);

      const first = service.enqueue(issue.id, 'start-pipeline');
      expect(first.status).toBe('running');

      const second = service.enqueue(issue.id, 'rebase');
      expect(second.status).toBe('pending');

      const status = service.getQueueStatus(issue.id) as any;
      expect(status.running).not.toBeNull();
      expect(status.running.id).toBe(first.taskId);
      expect(status.pending).toHaveLength(1);
      expect(status.pending[0].id).toBe(second.taskId);
    });
  });

  describe('cancel', () => {
    it('should cancel a pending task and return true', () => {
      const service = createService(1);

      const blockerProject = setupProject();
      const blockerIssue = setupIssue(blockerProject.id);
      service.enqueue(blockerIssue.id, 'start-pipeline');

      const project = setupProject();
      const issue = setupIssue(project.id);
      const result = service.enqueue(issue.id, 'start-pipeline');
      expect(result.status).toBe('pending');

      const cancelled = service.cancel(result.taskId);
      expect(cancelled).toBe(true);

      const dbRecord = taskQueueRepo.findById(result.taskId);
      expect(dbRecord!.status).toBe('cancelled');
    });

    it('should return false when cancelling a running task', () => {
      const project = setupProject();
      const issue = setupIssue(project.id);

      const service = createService();
      const result = service.enqueue(issue.id, 'start-pipeline');
      expect(result.status).toBe('running');

      const cancelled = service.cancel(result.taskId);
      expect(cancelled).toBe(false);
    });

    it('should return false for non-existent task', () => {
      const service = createService();
      expect(service.cancel('non-existent-id')).toBe(false);
    });

    it('should remove cancelled task from pending queue', () => {
      const service = createService(1);

      const blockerProject = setupProject();
      const blockerIssue = setupIssue(blockerProject.id);
      service.enqueue(blockerIssue.id, 'start-pipeline');

      const project = setupProject();
      const issue = setupIssue(project.id);
      const task = service.enqueue(issue.id, 'start-pipeline');
      service.cancel(task.taskId);

      const status = service.getQueueStatus(issue.id) as any;
      expect(status.pending).toHaveLength(0);
    });
  });

  describe('cancelAll', () => {
    it('should cancel all pending tasks and force-stop running task', () => {
      const project = setupProject();
      const issue = setupIssue(project.id);

      const service = createService();
      const running = service.enqueue(issue.id, 'start-pipeline');
      const pending1 = service.enqueue(issue.id, 'rebase');
      const pending2 = service.enqueue(issue.id, 'resume-pipeline');

      expect(running.status).toBe('running');
      expect(pending1.status).toBe('pending');
      expect(pending2.status).toBe('pending');

      service.cancelAll(issue.id);

      const runningDb = taskQueueRepo.findById(running.taskId);
      const pending1Db = taskQueueRepo.findById(pending1.taskId);
      const pending2Db = taskQueueRepo.findById(pending2.taskId);

      expect(runningDb!.status).toBe('cancelled');
      expect(pending1Db!.status).toBe('cancelled');
      expect(pending2Db!.status).toBe('cancelled');

      const status = service.getQueueStatus(issue.id) as any;
      expect(status.running).toBeNull();
      expect(status.pending).toHaveLength(0);
    });

    it('should release slot after cancelAll', () => {
      const project = setupProject();
      const issue = setupIssue(project.id);

      const service = createService(1);
      service.enqueue(issue.id, 'start-pipeline');

      const globalBefore = service.getQueueStatus() as any;
      expect(globalBefore.totalRunning).toBe(1);

      service.cancelAll(issue.id);

      const globalAfter = service.getQueueStatus() as any;
      expect(globalAfter.totalRunning).toBe(0);
    });
  });

  describe('getQueueStatus', () => {
    it('should return issue-specific queue status', () => {
      const service = createService();

      const project = setupProject();
      const issue = setupIssue(project.id);
      service.enqueue(issue.id, 'start-pipeline');
      service.enqueue(issue.id, 'rebase');

      const status = service.getQueueStatus(issue.id) as any;
      expect(status.running).not.toBeNull();
      expect(status.pending).toHaveLength(1);
      expect(status.queueLength).toBe(1);
    });

    it('should return global queue status when no issueId', () => {
      const project = setupProject();
      const issue = setupIssue(project.id);

      const service = createService();
      service.enqueue(issue.id, 'start-pipeline');

      const status = service.getQueueStatus() as any;
      expect(status.totalRunning).toBe(1);
      expect(status.totalPending).toBe(0);
      expect(status.maxSlots).toBe(8);
      expect(status.issues).toBeInstanceOf(Map);
    });

    it('should return empty status for issue with no tasks', () => {
      const service = createService();
      const status = service.getQueueStatus('non-existent') as any;
      expect(status.running).toBeNull();
      expect(status.pending).toHaveLength(0);
      expect(status.queueLength).toBe(0);
    });
  });

  describe('recovery on restart', () => {
    it('should mark awaiting running tasks as completed', () => {
      const project = setupProject();
      const issue = setupIssue(project.id);

      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.setApprovalState(issue.id, {
        stage: Stage.Plan,
        status: 'awaiting',
        requestedAt: new Date().toISOString(),
      });

      const record = taskQueueRepo.insert({
        issueId: issue.id,
        issueNumber: issue.number,
        projectId: project.id,
        taskType: 'start-pipeline',
        priority: 0,
      });
      taskQueueRepo.updateStatus(record.id, 'running', {
        startedAt: new Date().toISOString(),
      });

      const service = createService();
      service.recoverFromQueue();

      const recovered = taskQueueRepo.findById(record.id);
      expect(recovered!.status).toBe('completed');
      expect(recovered!.result).toBe('awaiting_approval');

      const recoveredIssue = issueRepo.findById(issue.id);
      expect(recoveredIssue!.stage).toBe(Stage.Plan);
    });

    it('should mark mid-execution running tasks as failed and set issue interrupted', () => {
      const project = setupProject();
      const issue = setupIssue(project.id);

      issueRepo.updateStage(issue.id, Stage.Build);

      const record = taskQueueRepo.insert({
        issueId: issue.id,
        issueNumber: issue.number,
        projectId: project.id,
        taskType: 'start-pipeline',
        priority: 0,
      });
      taskQueueRepo.updateStatus(record.id, 'running', {
        startedAt: new Date().toISOString(),
      });

      const service = createService();
      service.recoverFromQueue();

      const recovered = taskQueueRepo.findById(record.id);
      expect(recovered!.status).toBe('failed');
      expect(recovered!.result).toBe('Server restarted');

      const recoveredIssue = issueRepo.findById(issue.id);
      expect(recoveredIssue!.status).toBe(IssueStatus.Interrupted);
    });

    it('should reload pending tasks into in-memory queue and schedule them', () => {
      const project = setupProject();
      const issue = setupIssue(project.id);

      taskQueueRepo.insert({
        issueId: issue.id,
        issueNumber: issue.number,
        projectId: project.id,
        taskType: 'start-pipeline',
        priority: 0,
      });

      const service = createService();
      service.recoverFromQueue();

      const globalStatus = service.getQueueStatus() as any;
      expect(globalStatus.totalRunning).toBe(1);
      expect(globalStatus.totalPending).toBe(0);

      const runningRecords = taskQueueRepo.findAllRunning();
      expect(runningRecords).toHaveLength(1);
      expect(runningRecords[0].issueId).toBe(issue.id);
    });

    it('should not reclassify tasks started during queue recovery when issue recovery also runs', () => {
      const project = setupProject();
      const issue = setupIssue(project.id);
      issueRepo.updateStage(issue.id, Stage.Integrate);
      issueRepo.setMergeState(issue.id, MergeState.Merged);

      taskQueueRepo.insert({
        issueId: issue.id,
        issueNumber: issue.number,
        projectId: project.id,
        taskType: 'resume-pipeline',
        priority: 0,
      });

      const service = createService();
      service.recoverFromQueue();
      service.recoverIssues();

      const runningRecords = taskQueueRepo.findAllRunning();
      expect(runningRecords).toHaveLength(1);
      expect(runningRecords[0].issueId).toBe(issue.id);

      const recoveredIssue = issueRepo.findById(issue.id);
      expect(recoveredIssue!.status).not.toBe(IssueStatus.Interrupted);
    });
  });

  describe('execution-time skip', () => {
    it('should complete task with result skipped when issue not in draft/backlog for start-pipeline', async () => {
      const project = setupProject();
      const issue = setupIssue(project.id);
      issueRepo.updateStage(issue.id, Stage.Build);

      const service = createService();
      const result = service.enqueue(issue.id, 'start-pipeline');

      await new Promise((r) => setTimeout(r, 50));

      const dbRecord = taskQueueRepo.findById(result.taskId);
      expect(dbRecord!.status).toBe('completed');
      expect(dbRecord!.result).toBe('skipped');
    });

    it('should release slot after skipped task', async () => {
      const project = setupProject();
      const issue = setupIssue(project.id);
      issueRepo.updateStage(issue.id, Stage.Build);

      const service = createService(1);
      service.enqueue(issue.id, 'start-pipeline');

      await new Promise((r) => setTimeout(r, 50));

      const globalStatus = service.getQueueStatus() as any;
      expect(globalStatus.totalRunning).toBe(0);
    });

    it('should skip start-pipeline when issue is blocked', async () => {
      const project = setupProject();
      const issue = setupIssue(project.id);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);

      const service = createService();
      const result = service.enqueue(issue.id, 'start-pipeline');

      await new Promise((r) => setTimeout(r, 50));

      const dbRecord = taskQueueRepo.findById(result.taskId);
      expect(dbRecord!.status).toBe('completed');
      expect(dbRecord!.result).toBe('skipped');
    });

    it('should skip resume-pipeline when issue is already done', async () => {
      const project = setupProject();
      const issue = setupIssue(project.id);
      issueRepo.updateStage(issue.id, Stage.Done);
      issueRepo.updateStatus(issue.id, IssueStatus.Active);

      const service = createService();
      const result = service.enqueue(issue.id, 'resume-pipeline');

      await new Promise((r) => setTimeout(r, 50));

      const dbRecord = taskQueueRepo.findById(result.taskId);
      expect(dbRecord!.status).toBe('completed');
      expect(dbRecord!.result).toBe('skipped');
    });

    it('should execute rebase for integrate-stage issues', async () => {
      const project = setupProject();
      touchFetchCache(project.path);
      const issue = setupIssue(project.id);
      issueRepo.updateStage(issue.id, Stage.Integrate);
      issueRepo.updateStatus(issue.id, IssueStatus.Active);

      const wtManager = createHangingWorktreeManager();
      wtManager.exists.mockReturnValue(true);
      wtManager.canFastForward.mockResolvedValue(false);
      wtManager.rebaseOntoMaster.mockResolvedValue({ success: true, conflicts: [] });

      const service = createService(1, wtManager);
      const result = service.enqueue(issue.id, 'rebase');

      await new Promise((r) => setTimeout(r, 50));

      const dbRecord = taskQueueRepo.findById(result.taskId);
      expect(dbRecord!.status).toBe('completed');
      expect(dbRecord!.result).toBe('success');
      expect(wtManager.rebaseOntoMaster).toHaveBeenCalledWith(
        project.path,
        project.name,
        issue.number,
        project.baseBranch,
        { abortOnConflict: false },
      );
    });
  });

  describe('slot freed on task completion triggers schedule', () => {
    it('should start pending task when slot becomes available', async () => {
      const service = createService(1);

      const project1 = setupProject();
      const issue1 = setupIssue(project1.id);

      const project2 = setupProject();
      const issue2 = setupIssue(project2.id);

      const first = service.enqueue(issue1.id, 'start-pipeline');
      expect(first.status).toBe('running');

      const second = service.enqueue(issue2.id, 'start-pipeline');
      expect(second.status).toBe('pending');

      service.cancelAll(issue1.id);

      await new Promise((r) => setTimeout(r, 50));

      const secondDb = taskQueueRepo.findById(second.taskId);
      expect(secondDb!.status).toBe('running');
    });
  });
});
