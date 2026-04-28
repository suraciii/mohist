import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { MergeQueue, MergeEntry } from '../../src/git/merge-queue';
import { MergeState } from '../../src/types';
import { WorktreeManager } from '../../src/git/worktree-manager';
import { EventBus } from '../../src/services/event-bus';
import { IssueRepo } from '../../src/db/issue-repo';
import { ProjectRepo } from '../../src/db/project-repo';
import { DatabaseManager } from '../../src/db/database';
import { initializeDatabase } from '../../src/db/migrations';
import { IssueService } from '../../src/services/issue-service';

function createMockWorktreeManager() {
  return {
    rebaseOntoMaster: vi.fn().mockResolvedValue({ success: true }),
    mergeBack: vi.fn().mockResolvedValue({ success: true, message: 'Merged' }),
    remove: vi.fn().mockResolvedValue(undefined),
    getPath: vi.fn().mockReturnValue('/test/worktree'),
    exists: vi.fn().mockReturnValue(true),
    create: vi.fn().mockResolvedValue('/test/worktree'),
  } as unknown as WorktreeManager;
}

describe('MergeQueue rebase-first flow', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let issueService: IssueService;
  let eventBus: EventBus;
  let worktreeManager: ReturnType<typeof createMockWorktreeManager>;

  const PROJECT_PATH = '/test/project';
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
  });

  afterEach(() => {
    db.close();
  });

  function setupProject() {
    return projectRepo.create({ name: PROJECT_NAME, path: PROJECT_PATH, baseBranch: BASE_BRANCH });
  }

  function createQueue(projectId: string) {
    return new MergeQueue({
      worktreeManager,
      eventBus,
      issueRepo,
      getProjectPath: (pid: string) => {
        if (pid !== projectId) return null;
        return { path: PROJECT_PATH, name: PROJECT_NAME, baseBranch: BASE_BRANCH };
      },
    });
  }

  async function waitForSettle(ms = 100): Promise<void> {
    await new Promise((r) => setTimeout(r, ms));
  }

  function captureEvents(eventBus: EventBus, ...types: string[]) {
    const captured: Array<{ type: string; payload: any }> = [];
    for (const t of types) {
      eventBus.on(t as any, ((payload: any) => {
        captured.push({ type: t, payload });
      }) as any);
    }
    return captured;
  }

  describe('rebase success → ff-merge → merged state transition', () => {
    it('should transition pending → rebasing → merging → merged', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      vi.spyOn(queue as any, 'branchHasCommits').mockResolvedValue(true);
      vi.spyOn(queue as any, 'runBuildVerification').mockResolvedValue(true);

      const events = captureEvents(eventBus,
        'rebase_started', 'rebase_completed', 'merge_started', 'merge_completed');

      queue.enqueue(project.id, issue.number);
      await waitForSettle();

      expect(worktreeManager.rebaseOntoMaster).toHaveBeenCalledWith(
        '/test/worktree', BASE_BRANCH,
      );
      expect(worktreeManager.mergeBack).toHaveBeenCalledWith(
        PROJECT_PATH, PROJECT_NAME, issue.number, BASE_BRANCH,
      );

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe(MergeState.Merged);

      const types = events.map((e) => e.type);
      expect(types).toContain('rebase_started');
      expect(types).toContain('rebase_completed');
      expect(types).toContain('merge_started');
      expect(types).toContain('merge_completed');

      expect(worktreeManager.remove).toHaveBeenCalledWith(
        PROJECT_PATH, PROJECT_NAME, issue.number,
      );
    });
  });

  describe('rebase conflict → abort → conflict state with conflictingFiles', () => {
    it('should set conflict state and emit rebase_conflict with file list', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      vi.spyOn(queue as any, 'branchHasCommits').mockResolvedValue(true);
      worktreeManager.rebaseOntoMaster = vi.fn().mockResolvedValue({
        success: false,
        message: 'Rebase conflict: CONFLICT (content): Merge conflict in src/foo.ts',
        conflictingFiles: ['src/foo.ts', 'src/bar.ts'],
      });

      const events = captureEvents(eventBus, 'rebase_conflict', 'rebase_completed', 'merge_completed');

      queue.enqueue(project.id, issue.number);
      await waitForSettle();

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe(MergeState.Conflict);

      expect(events.filter((e) => e.type === 'rebase_conflict')).toHaveLength(1);
      const conflictEvent = events.find((e) => e.type === 'rebase_conflict');
      expect(conflictEvent?.payload.conflictingFiles).toEqual(['src/foo.ts', 'src/bar.ts']);
      expect(conflictEvent?.payload.issueNumber).toBe(issue.number);

      expect(events.filter((e) => e.type === 'rebase_completed')).toHaveLength(0);
      expect(events.filter((e) => e.type === 'merge_completed')).toHaveLength(0);
    });

    it('should NOT call mergeBack when rebase conflicts', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      vi.spyOn(queue as any, 'branchHasCommits').mockResolvedValue(true);
      worktreeManager.rebaseOntoMaster = vi.fn().mockResolvedValue({
        success: false,
        message: 'Conflict',
        conflictingFiles: [],
      });

      queue.enqueue(project.id, issue.number);
      await waitForSettle();

      expect(worktreeManager.mergeBack).not.toHaveBeenCalled();
    });
  });

  describe('no-commits → skip merge → merged state', () => {
    it('should skip rebase and merge when branch has no commits', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      vi.spyOn(queue as any, 'branchHasCommits').mockResolvedValue(false);

      const events = captureEvents(eventBus, 'merge_completed', 'rebase_started');

      queue.enqueue(project.id, issue.number);
      await waitForSettle();

      expect(worktreeManager.rebaseOntoMaster).not.toHaveBeenCalled();
      expect(worktreeManager.mergeBack).not.toHaveBeenCalled();
      expect(worktreeManager.remove).toHaveBeenCalledWith(
        PROJECT_PATH, PROJECT_NAME, issue.number,
      );

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe(MergeState.Merged);

      expect(events.filter((e) => e.type === 'merge_completed')).toHaveLength(1);
      expect(events.filter((e) => e.type === 'rebase_started')).toHaveLength(0);
    });
  });

  describe('auto-retry triggers when master HEAD changed', () => {
    it('should re-enqueue conflict issue when master HEAD changes', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      issueRepo.setMergeState(issue.id, MergeState.Conflict);

      let headSeq = 0;
      const queue = createQueue(project.id);
      vi.spyOn(queue as any, 'getMasterHead').mockImplementation(async () => {
        headSeq++;
        return `sha-${headSeq}`;
      });
      vi.spyOn(queue as any, 'processNext').mockImplementation(async () => {});

      (queue as any).processing = true;
      queue.recoverFromDB();
      (queue as any).processing = false;

      const events = captureEvents(eventBus, 'rebase_retry');

      await (queue as any).checkBlockedIssues();

      expect(events).toHaveLength(1);
      expect(events[0].payload.attempt).toBe(1);
      expect(events[0].payload.issueNumber).toBe(issue.number);

      const entry = queue.getStatus().find((e: MergeEntry) => e.issueNumber === issue.number);
      expect(entry?.mergeState).toBe(MergeState.Pending);
      expect(entry?.lastAttemptHead).toBe('sha-1');
    });

    it('should NOT retry when master HEAD unchanged', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      issueRepo.setMergeState(issue.id, MergeState.Conflict);

      const queue = createQueue(project.id);
      vi.spyOn(queue as any, 'getMasterHead').mockResolvedValue('same-sha');

      (queue as any).processing = true;
      queue.recoverFromDB();
      (queue as any).processing = false;

      const entry = queue.getStatus().find((e: MergeEntry) => e.issueNumber === issue.number);
      entry!.lastAttemptHead = 'same-sha';

      const events = captureEvents(eventBus, 'rebase_retry');

      await (queue as any).checkBlockedIssues();

      expect(events).toHaveLength(0);
      expect(entry?.mergeState).toBe(MergeState.Conflict);
    });
  });

  describe('retry count limit (5) → blocked state', () => {
    it('should mark blocked after retryCount reaches MAX (5)', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      issueRepo.setMergeState(issue.id, MergeState.Conflict);

      const queue = createQueue(project.id);
      vi.spyOn(queue as any, 'getMasterHead').mockResolvedValue('new-sha');

      (queue as any).processing = true;
      queue.recoverFromDB();
      (queue as any).processing = false;

      const entry = queue.getStatus().find((e: MergeEntry) => e.issueNumber === issue.number);
      expect(entry).toBeDefined();
      entry!.retryCount = 5;

      const events = captureEvents(eventBus, 'merge_blocked');

      await (queue as any).checkBlockedIssues();

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe(MergeState.Blocked);

      expect(events).toHaveLength(1);
      expect(events[0].payload.issueNumber).toBe(issue.number);
      expect(events[0].payload.reason).toContain('5');
    });

    it('should NOT re-mark blocked if already blocked', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      issueRepo.setMergeState(issue.id, MergeState.Blocked);

      const queue = createQueue(project.id);
      vi.spyOn(queue as any, 'getMasterHead').mockResolvedValue('new-sha');

      (queue as any).processing = true;
      queue.recoverFromDB();
      (queue as any).processing = false;

      const entry = queue.getStatus().find((e: MergeEntry) => e.issueNumber === issue.number);
      entry!.retryCount = 5;

      const events = captureEvents(eventBus, 'merge_blocked');

      await (queue as any).checkBlockedIssues();

      expect(events).toHaveLength(0);
    });
  });

  describe('manual retry resets retryCount to 0', () => {
    it('should reset retryCount and set state to pending', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      issueRepo.setMergeState(issue.id, MergeState.Blocked);

      const queue = createQueue(project.id);
      vi.spyOn(queue as any, 'processNext').mockImplementation(async () => {});

      (queue as any).processing = true;
      queue.recoverFromDB();
      (queue as any).processing = false;

      const entry = queue.getStatus().find((e: MergeEntry) => e.issueNumber === issue.number);
      entry!.retryCount = 4;

      const result = queue.retry(issue.number);
      expect(result).toBe(true);

      const afterRetry = queue.getStatus().find((e: MergeEntry) => e.issueNumber === issue.number);
      expect(afterRetry?.retryCount).toBe(0);
      expect(afterRetry?.mergeState).toBe(MergeState.Pending);

      const dbIssue = issueRepo.findById(issue.id);
      expect(dbIssue?.mergeState).toBe(MergeState.Pending);
    });

    it('should reject retry from non-retryable states', () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      queue.enqueue(project.id, issue.number);

      expect(queue.retry(issue.number)).toBe(false);
    });

    it('should reject retry for unknown issue', () => {
      const queue = createQueue('nonexistent');
      expect(queue.retry(9999)).toBe(false);
    });
  });

  describe('conflict-aware ordering with file overlap in pickNext', () => {
    it('should process FIFO candidate first when file overlap detected', async () => {
      const project = setupProject();
      const issue1 = issueService.create({ projectId: project.id, title: 'Issue 1' });
      const issue2 = issueService.create({ projectId: project.id, title: 'Issue 2' });

      const queue = createQueue(project.id);
      vi.spyOn(queue as any, 'processNext').mockImplementation(async () => {});

      queue.enqueue(project.id, issue1.number);
      queue.enqueue(project.id, issue2.number);

      vi.spyOn(queue as any, 'getChangedFiles').mockImplementation(
        async (_path: string, branch: string) => {
          if (branch === 'mo/issue-1') return ['src/shared.ts', 'src/a.ts'];
          if (branch === 'mo/issue-2') return ['src/shared.ts', 'src/b.ts'];
          return [];
        },
      );

      const picked = await (queue as any).pickNext();

      expect(picked.issueNumber).toBe(issue1.number);
    });

    it('should return FIFO candidate when no overlap exists', async () => {
      const project = setupProject();
      const issue1 = issueService.create({ projectId: project.id, title: 'Issue 1' });
      const issue2 = issueService.create({ projectId: project.id, title: 'Issue 2' });

      const queue = createQueue(project.id);
      vi.spyOn(queue as any, 'processNext').mockImplementation(async () => {});

      queue.enqueue(project.id, issue1.number);
      queue.enqueue(project.id, issue2.number);

      vi.spyOn(queue as any, 'getChangedFiles').mockImplementation(
        async (_path: string, branch: string) => {
          if (branch === 'mo/issue-1') return ['src/a.ts'];
          if (branch === 'mo/issue-2') return ['src/b.ts'];
          return [];
        },
      );

      const picked = await (queue as any).pickNext();

      expect(picked.issueNumber).toBe(issue1.number);
    });

    it('should fall back to FIFO when getChangedFiles fails', async () => {
      const project = setupProject();
      const issue1 = issueService.create({ projectId: project.id, title: 'Issue 1' });
      const issue2 = issueService.create({ projectId: project.id, title: 'Issue 2' });

      const queue = createQueue(project.id);
      vi.spyOn(queue as any, 'processNext').mockImplementation(async () => {});

      queue.enqueue(project.id, issue1.number);
      queue.enqueue(project.id, issue2.number);

      vi.spyOn(queue as any, 'getChangedFiles').mockResolvedValue(undefined);

      const picked = await (queue as any).pickNext();

      expect(picked.issueNumber).toBe(issue1.number);
    });

    it('should return only pending entry when single entry in queue', async () => {
      const project = setupProject();
      const issue1 = issueService.create({ projectId: project.id, title: 'Issue 1' });

      const queue = createQueue(project.id);
      vi.spyOn(queue as any, 'processNext').mockImplementation(async () => {});

      queue.enqueue(project.id, issue1.number);

      const picked = await (queue as any).pickNext();

      expect(picked.issueNumber).toBe(issue1.number);
    });
  });

  describe('startAutoRetry / stopAutoRetry', () => {
    it('should start and stop auto-retry timer', () => {
      const queue = createQueue('proj');

      queue.startAutoRetry(100);
      expect((queue as any).autoRetryTimer).not.toBeNull();

      queue.stopAutoRetry();
      expect((queue as any).autoRetryTimer).toBeNull();
    });

    it('should not start duplicate timer', () => {
      const queue = createQueue('proj');

      queue.startAutoRetry(100);
      const firstTimer = (queue as any).autoRetryTimer;

      queue.startAutoRetry(100);
      expect((queue as any).autoRetryTimer).toBe(firstTimer);

      queue.stopAutoRetry();
    });
  });

  describe('recoverFromDB with rebase states', () => {
    it('should recover rebasing state as pending', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test' });
      issueRepo.setMergeState(issue.id, MergeState.Rebasing);

      const queue = createQueue(project.id);
      vi.spyOn(queue as any, 'branchHasCommits').mockResolvedValue(true);
      vi.spyOn(queue as any, 'runBuildVerification').mockResolvedValue(true);

      queue.recoverFromDB();
      await waitForSettle();

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe(MergeState.Merged);
    });
  });
});
