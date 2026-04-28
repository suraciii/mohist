import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { IssueService } from '../src/services/issue-service';
import { EventBus } from '../src/services/event-bus';
import { MergeQueue } from '../src/git/merge-queue';
import { WorktreeManager } from '../src/git/worktree-manager';

vi.mock('child_process', async (importOriginal) => {
  const actual = await importOriginal<typeof import('child_process')>();
  return {
    ...actual,
    execFile: vi.fn(),
  };
});

import { execFile } from 'child_process';

const execFileMock = vi.mocked(execFile);

function createMockWorktreeManager() {
  return {
    rebaseOntoMaster: vi.fn().mockResolvedValue({ success: true }),
    mergeBack: vi.fn().mockResolvedValue({ success: true, message: 'Merged' }),
    remove: vi.fn().mockResolvedValue(undefined),
    exists: vi.fn().mockReturnValue(true),
    create: vi.fn().mockResolvedValue('/tmp/worktree'),
    getPath: vi.fn().mockReturnValue('/tmp/worktree'),
  } as unknown as WorktreeManager;
}

function createMergeQueue(deps: {
  worktreeManager: WorktreeManager;
  eventBus: EventBus;
  issueRepo: IssueRepo;
  getProjectPath: (projectId: string) => { path: string; name: string; baseBranch: string } | null;
}) {
  return new MergeQueue(deps);
}

describe('MergeQueue', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let issueService: IssueService;
  let eventBus: EventBus;
  let worktreeManager: ReturnType<typeof createMockWorktreeManager>;

  const PROJECT_PATH = '/tmp/test-project';
  const PROJECT_NAME = 'test-project';
  const BASE_BRANCH = 'main';

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    issueService = new IssueService(issueRepo);
    eventBus = new EventBus();
    worktreeManager = createMockWorktreeManager();
    execFileMock.mockReset();
  });

  afterEach(() => {
    db.close();
  });

  function setupProject() {
    return projectRepo.create({ name: PROJECT_NAME, path: PROJECT_PATH, baseBranch: BASE_BRANCH });
  }

  function createQueue(projectId: string) {
    return createMergeQueue({
      worktreeManager,
      eventBus,
      issueRepo,
      getProjectPath: (pid: string) => {
        if (pid !== projectId) return null;
        return { path: PROJECT_PATH, name: PROJECT_NAME, baseBranch: BASE_BRANCH };
      },
    });
  }

  async function waitForQueueToSettle(queue: MergeQueue): Promise<void> {
    for (let i = 0; i < 50; i++) {
      await new Promise((r) => setTimeout(r, 10));
    }
  }

  function setupMockExecFile(overrides?: (cmd: string, args: string[], opts: any, cb: any) => void) {
    execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
      if (cmd === 'git' && args?.[0] === 'log' && args?.[2] === '--oneline') {
        cb?.(null, { stdout: 'abc123 commit message\n', stderr: '' });
        return undefined as any;
      }
      if (overrides) {
        overrides(cmd, args, opts, cb);
        return undefined as any;
      }
      cb?.(null, { stdout: '', stderr: '' });
      return undefined as any;
    });
  }

  describe('enqueue', () => {
    it('should set mergeState=pending and emit merge_queued', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      const events: any[] = [];
      eventBus.on('merge_queued', (data) => events.push(data));

      queue.enqueue(project.id, issue.number);

      await waitForQueueToSettle(queue);

      expect(events).toHaveLength(1);
      expect(events[0].issueId).toBe(issue.id);
      expect(events[0].issueNumber).toBe(issue.number);
      expect(events[0].position).toBe(1);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('pending');
    });

    it('should ignore duplicate enqueue for same issue', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });

      worktreeManager.rebaseOntoMaster = vi.fn().mockImplementation(async () => {
        await new Promise((r) => setTimeout(r, 100));
        return { success: true };
      });
      worktreeManager.mergeBack = vi.fn().mockImplementation(async () => {
        await new Promise((r) => setTimeout(r, 100));
        return { success: true, message: 'Merged' };
      });

      setupMockExecFile();

      const queue = createQueue(project.id);
      const events: any[] = [];
      eventBus.on('merge_queued', (data) => events.push(data));

      queue.enqueue(project.id, issue.number);
      queue.enqueue(project.id, issue.number);

      await waitForQueueToSettle(queue);

      expect(events).toHaveLength(1);
    });

    it('should do nothing if issue not found', () => {
      const project = setupProject();
      const queue = createQueue(project.id);

      const events: any[] = [];
      eventBus.on('merge_queued', (data) => events.push(data));

      queue.enqueue(project.id, 9999);

      expect(events).toHaveLength(0);
      expect(queue.getStatus()).toHaveLength(0);
    });
  });

  describe('processNext → merged lifecycle', () => {
    it('should call rebaseOntoMaster, mergeBack and set mergeState=merged on success', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      setupMockExecFile();

      const completedEvents: any[] = [];
      eventBus.on('merge_completed', (data) => completedEvents.push(data));

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      expect(worktreeManager.rebaseOntoMaster).toHaveBeenCalledWith(
        '/tmp/worktree',
        BASE_BRANCH,
      );
      expect(worktreeManager.mergeBack).toHaveBeenCalledWith(
        PROJECT_PATH,
        PROJECT_NAME,
        issue.number,
        BASE_BRANCH,
      );

      expect(completedEvents).toHaveLength(1);
      expect(completedEvents[0].issueNumber).toBe(issue.number);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('merged');

      expect(worktreeManager.remove).toHaveBeenCalledWith(
        PROJECT_PATH,
        PROJECT_NAME,
        issue.number,
      );
    });

    it('should process items serially in FIFO order', async () => {
      const project = setupProject();
      const issue1 = issueService.create({ projectId: project.id, title: 'Issue 1' });
      const issue2 = issueService.create({ projectId: project.id, title: 'Issue 2' });

      const order: number[] = [];
      worktreeManager.rebaseOntoMaster = vi.fn().mockImplementation(async () => {
        await new Promise((r) => setTimeout(r, 10));
        return { success: true };
      });
      worktreeManager.mergeBack = vi.fn().mockImplementation(async (_p: string, _n: string, num: number) => {
        order.push(num);
        await new Promise((r) => setTimeout(r, 20));
        return { success: true, message: 'Merged' };
      });

      const queue = createQueue(project.id);

      setupMockExecFile();

      queue.enqueue(project.id, issue1.number);
      queue.enqueue(project.id, issue2.number);

      await waitForQueueToSettle(queue);

      expect(order).toEqual([issue1.number, issue2.number]);

      const u1 = issueRepo.findById(issue1.id);
      const u2 = issueRepo.findById(issue2.id);
      expect(u1?.mergeState).toBe('merged');
      expect(u2?.mergeState).toBe('merged');
    });
  });

  describe('build verification', () => {
    it('should run npm run build after merge and set merged on success', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      setupMockExecFile();

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      const buildCalls = execFileMock.mock.calls.filter(
        (c: any) => c[0] === 'npm' && c[1]?.[0] === 'run' && c[1]?.[1] === 'build',
      );
      expect(buildCalls.length).toBe(1);
      expect(buildCalls[0][2]?.cwd).toBe(PROJECT_PATH);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('merged');
    });

    it('should rollback and set build-failed when build fails', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      let buildCalled = false;
      setupMockExecFile((cmd, args, opts, cb) => {
        if (cmd === 'npm' && args?.[1] === 'build') {
          buildCalled = true;
          cb?.(new Error('Build failed') as any);
          return;
        }
        cb?.(null, { stdout: '', stderr: '' });
      });

      const failedEvents: any[] = [];
      eventBus.on('merge_failed', (data) => failedEvents.push(data));

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      expect(buildCalled).toBe(true);

      const resetCalls = execFileMock.mock.calls.filter(
        (c: any) => c[0] === 'git' && c[1]?.[0] === 'reset' && c[1]?.[1] === '--hard',
      );
      expect(resetCalls.length).toBe(1);
      expect(resetCalls[0][1]).toEqual(['reset', '--hard', 'HEAD~1']);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('build-failed');

      expect(failedEvents).toHaveLength(1);
      expect(failedEvents[0].reason).toBe('build-failed');
      expect(failedEvents[0].issueNumber).toBe(issue.number);
    });
  });

  describe('merge conflict', () => {
    it('should set build-failed when mergeBack fails after successful rebase', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      worktreeManager.rebaseOntoMaster = vi.fn().mockResolvedValue({ success: true });
      worktreeManager.mergeBack = vi.fn().mockResolvedValue({
        success: false,
        message: 'Fast-forward merge failed for issue #1: some error',
      });

      const failedEvents: any[] = [];
      eventBus.on('merge_failed', (data) => failedEvents.push(data));

      setupMockExecFile();
      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('build-failed');

      expect(failedEvents).toHaveLength(1);
      expect(failedEvents[0].reason).toBe('build-failed');
    });

    it('should set conflict state when rebase fails', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      worktreeManager.rebaseOntoMaster = vi.fn().mockResolvedValue({
        success: false,
        message: 'Rebase conflict: CONFLICT in file.ts',
        conflictingFiles: ['file.ts'],
      });

      setupMockExecFile();
      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('conflict');
    });
  });

  describe('retry', () => {
    it('should retry from build-failed state', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      let callCount = 0;
      worktreeManager.rebaseOntoMaster = vi.fn().mockResolvedValue({ success: true });
      worktreeManager.mergeBack = vi.fn().mockImplementation(async () => {
        callCount++;
        if (callCount === 1) {
          return { success: false, message: 'Build failed' };
        }
        return { success: true, message: 'Merged' };
      });

      setupMockExecFile();

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      const afterFirst = issueRepo.findById(issue.id);
      expect(afterFirst?.mergeState).toBe('build-failed');

      const retried = queue.retry(issue.number);
      expect(retried).toBe(true);

      await waitForQueueToSettle(queue);

      const afterRetry = issueRepo.findById(issue.id);
      expect(afterRetry?.mergeState).toBe('merged');

      expect(worktreeManager.mergeBack).toHaveBeenCalledTimes(2);
    });

    it('should retry from conflict state', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      let rebased = false;
      worktreeManager.rebaseOntoMaster = vi.fn().mockImplementation(async () => {
        if (!rebased) {
          rebased = true;
          return {
            success: false,
            message: 'Rebase conflict: CONFLICT in file.ts',
            conflictingFiles: ['file.ts'],
          };
        }
        return { success: true };
      });
      worktreeManager.mergeBack = vi.fn().mockResolvedValue({ success: true, message: 'Merged' });

      setupMockExecFile();

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      expect(issueRepo.findById(issue.id)?.mergeState).toBe('conflict');

      const retried = queue.retry(issue.number);
      expect(retried).toBe(true);

      await waitForQueueToSettle(queue);

      expect(issueRepo.findById(issue.id)?.mergeState).toBe('merged');
    });

    it('should return false for non-retryable states', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      queue.enqueue(project.id, issue.number);

      expect(queue.retry(issue.number)).toBe(false);
    });

    it('should return false for unknown issue number', () => {
      const project = setupProject();
      const queue = createQueue(project.id);

      expect(queue.retry(9999)).toBe(false);
    });
  });

  describe('getStatus', () => {
    it('should return entries sorted by enqueuedAt', async () => {
      const project = setupProject();
      const issue1 = issueService.create({ projectId: project.id, title: 'Issue 1' });
      const issue2 = issueService.create({ projectId: project.id, title: 'Issue 2' });

      worktreeManager.rebaseOntoMaster = vi.fn().mockImplementation(async () => {
        await new Promise((r) => setTimeout(r, 50));
        return { success: true };
      });
      worktreeManager.mergeBack = vi.fn().mockImplementation(async () => {
        await new Promise((r) => setTimeout(r, 50));
        return { success: true, message: 'Merged' };
      });

      setupMockExecFile();

      const queue = createQueue(project.id);

      queue.enqueue(project.id, issue1.number);
      queue.enqueue(project.id, issue2.number);

      const status = queue.getStatus();
      expect(status).toHaveLength(2);
      expect(status[0].issueNumber).toBe(issue1.number);
      expect(status[1].issueNumber).toBe(issue2.number);
    });
  });

  describe('recoverFromDB', () => {
    it('should re-enqueue issues with pending mergeState', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });

      issueRepo.setMergeState(issue.id, 'pending');

      setupMockExecFile();

      const queue = createQueue(project.id);

      queue.recoverFromDB();

      await waitForQueueToSettle(queue);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('merged');
    });

    it('should re-enqueue issues with merging state (reset to pending)', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });

      issueRepo.setMergeState(issue.id, 'merging');

      setupMockExecFile();

      const queue = createQueue(project.id);

      queue.recoverFromDB();

      await waitForQueueToSettle(queue);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('merged');

      expect(worktreeManager.rebaseOntoMaster).toHaveBeenCalledTimes(1);
      expect(worktreeManager.mergeBack).toHaveBeenCalledTimes(1);
    });

    it('should recover multiple issues and process them serially', async () => {
      const project = setupProject();
      const issue1 = issueService.create({ projectId: project.id, title: 'Issue 1' });
      const issue2 = issueService.create({ projectId: project.id, title: 'Issue 2' });

      issueRepo.setMergeState(issue1.id, 'pending');
      issueRepo.setMergeState(issue2.id, 'merging');

      setupMockExecFile();

      const queue = createQueue(project.id);

      queue.recoverFromDB();

      await waitForQueueToSettle(queue);

      expect(issueRepo.findById(issue1.id)?.mergeState).toBe('merged');
      expect(issueRepo.findById(issue2.id)?.mergeState).toBe('merged');
      expect(worktreeManager.rebaseOntoMaster).toHaveBeenCalledTimes(2);
      expect(worktreeManager.mergeBack).toHaveBeenCalledTimes(2);
    });

    it('should not recover issues that are already in queue', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });

      worktreeManager.rebaseOntoMaster = vi.fn().mockImplementation(async () => {
        await new Promise((r) => setTimeout(r, 200));
        return { success: true };
      });
      worktreeManager.mergeBack = vi.fn().mockImplementation(async () => {
        await new Promise((r) => setTimeout(r, 200));
        return { success: true, message: 'Merged' };
      });

      setupMockExecFile();

      const queue = createQueue(project.id);
      queue.enqueue(project.id, issue.number);

      queue.recoverFromDB();

      await waitForQueueToSettle(queue);

      expect(worktreeManager.rebaseOntoMaster).toHaveBeenCalledTimes(1);
      expect(worktreeManager.mergeBack).toHaveBeenCalledTimes(1);
    });

    it('should handle empty DB gracefully', () => {
      const project = setupProject();
      const queue = createQueue(project.id);

      expect(() => queue.recoverFromDB()).not.toThrow();
      expect(queue.getStatus()).toHaveLength(0);
    });
  });

  describe('project not found', () => {
    it('should fail with conflict when project path not found', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });

      const queue = createMergeQueue({
        worktreeManager,
        eventBus,
        issueRepo,
        getProjectPath: () => null,
      });

      const failedEvents: any[] = [];
      eventBus.on('merge_failed', (data) => failedEvents.push(data));

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('conflict');
      expect(failedEvents).toHaveLength(1);
    });
  });

  describe('merge_queued event position', () => {
    it('should report correct queue position', async () => {
      const project = setupProject();
      const issue1 = issueService.create({ projectId: project.id, title: 'Issue 1' });
      const issue2 = issueService.create({ projectId: project.id, title: 'Issue 2' });

      worktreeManager.rebaseOntoMaster = vi.fn().mockImplementation(async () => {
        await new Promise((r) => setTimeout(r, 100));
        return { success: true };
      });
      worktreeManager.mergeBack = vi.fn().mockImplementation(async () => {
        await new Promise((r) => setTimeout(r, 100));
        return { success: true, message: 'Merged' };
      });

      setupMockExecFile();

      const queue = createQueue(project.id);
      const positions: number[] = [];
      eventBus.on('merge_queued', (data) => {
        positions.push(data.position);
      });

      queue.enqueue(project.id, issue1.number);
      queue.enqueue(project.id, issue2.number);

      await waitForQueueToSettle(queue);

      expect(positions[0]).toBe(1);
      expect(positions[1]).toBeLessThanOrEqual(2);
    });
  });
});
