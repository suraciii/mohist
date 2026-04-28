import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { MergeQueue, MergeEntry } from '../../src/git/merge-queue';
import { WorktreeManager } from '../../src/git/worktree-manager';
import { EventBus } from '../../src/services/event-bus';
import { IssueRepo } from '../../src/db/issue-repo';
import { ProjectRepo } from '../../src/db/project-repo';
import { DatabaseManager } from '../../src/db/database';
import { initializeDatabase } from '../../src/db/migrations';
import { IssueService } from '../../src/services/issue-service';

describe('MergeQueue rebase-first flow', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let issueService: IssueService;
  let eventBus: EventBus;
  let worktreeManager: WorktreeManager;
  let queue: MergeQueue;
  let events: Array<{ type: string; payload: any }> = [];

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    issueService = new IssueService(issueRepo);
    eventBus = new EventBus();
    worktreeManager = new WorktreeManager();
    events = [];

    // Capture all events
    const origEmit = eventBus.emit.bind(eventBus);
    eventBus.emit = (type: string, payload: any) => {
      events.push({ type, payload });
      return origEmit(type, payload);
    };

    queue = new MergeQueue({
      worktreeManager,
      eventBus,
      issueRepo,
      getProjectPath: () => ({ path: '/test/project', name: 'test-project', baseBranch: 'main' }),
    });
  });

  afterEach(() => {
    db.close();
  });

  describe('T-001: rebase success → ff-merge → merged', () => {
    it('should transition through rebasing → merging → merged on success', async () => {
      const project = projectRepo.create({ name: 'Test', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      issueRepo.setMergeState(issue.id, 'pending');

      vi.spyOn(worktreeManager, 'rebaseOntoMaster').mockResolvedValue({ success: true });
      vi.spyOn(worktreeManager, 'mergeBack').mockResolvedValue({ success: true, message: 'Merged' });
      vi.spyOn(queue as any, 'runBuildVerification').mockResolvedValue(true);
      vi.spyOn(worktreeManager, 'remove').mockResolvedValue(undefined);
      vi.spyOn(worktreeManager, 'getPath').mockReturnValue('/test/worktree');
      vi.spyOn(queue as any, 'branchHasCommits').mockResolvedValue(true);

      queue.enqueue(project.id, issue.number);
      await new Promise((resolve) => setTimeout(resolve, 50));

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('merged');

      const mergeCompletedEvent = events.find((e) => e.type === 'merge_completed');
      expect(mergeCompletedEvent).toBeDefined();
      expect(mergeCompletedEvent?.payload.issueNumber).toBe(issue.number);
    });
  });

  describe('T-002: rebase conflict → abort → conflict state', () => {
    it('should detect conflict, abort rebase, and set conflict state with files', async () => {
      const project = projectRepo.create({ name: 'Test', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      issueRepo.setMergeState(issue.id, 'pending');

      vi.spyOn(worktreeManager, 'rebaseOntoMaster').mockResolvedValue({
        success: false,
        message: 'Rebase conflict',
        conflictingFiles: ['src/conflict.ts'],
      });
      vi.spyOn(worktreeManager, 'getPath').mockReturnValue('/test/worktree');
      vi.spyOn(queue as any, 'branchHasCommits').mockResolvedValue(true);

      queue.enqueue(project.id, issue.number);
      await new Promise((resolve) => setTimeout(resolve, 50));

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('conflict');

      const conflictEvent = events.find((e) => e.type === 'rebase_conflict');
      expect(conflictEvent).toBeDefined();
      expect(conflictEvent?.payload.conflictingFiles).toContain('src/conflict.ts');
    });
  });

  describe('T-003: no-commits → skip merge → merged', () => {
    it('should skip rebase/merge when branch has no commits', async () => {
      const project = projectRepo.create({ name: 'Test', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      issueRepo.setMergeState(issue.id, 'pending');

      vi.spyOn(queue as any, 'branchHasCommits').mockResolvedValue(false);
      vi.spyOn(worktreeManager, 'remove').mockResolvedValue(undefined);

      queue.enqueue(project.id, issue.number);
      await new Promise((resolve) => setTimeout(resolve, 50));

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('merged');

      const mergeCompletedEvent = events.find((e) => e.type === 'merge_completed');
      expect(mergeCompletedEvent).toBeDefined();
    });
  });

  describe('T-004: auto-retry triggers when master HEAD changed', () => {
    it('should re-enqueue conflict issue when master HEAD changes', async () => {
      const project = projectRepo.create({ name: 'Test', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      issueRepo.setMergeState(issue.id, 'conflict');

      let headCallCount = 0;
      vi.spyOn(queue as any, 'getMasterHead').mockImplementation(async () => {
        headCallCount++;
        return headCallCount === 1 ? 'old-head' : 'new-head';
      });

      // Prevent processNext from running during recovery
      (queue as any).processing = true;
      queue.recoverFromDB();

      // Verify issue is in queue
      const queueStatus = queue.getStatus();
      expect(queueStatus.length).toBe(1);
      expect(queueStatus[0].mergeState).toBe('conflict');

      // First check: sets lastAttemptHead to 'old-head', increments retryCount to 1, changes state to pending
      await (queue as any).checkBlockedIssues();
      let updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('pending');

      // Manually set back to conflict to test HEAD change detection
      const entry = queue.getStatus().find((e: MergeEntry) => e.issueNumber === issue.number);
      entry!.mergeState = 'conflict';
      entry!.lastAttemptHead = 'old-head';
      issueRepo.setMergeState(issue.id, 'conflict');

      // Second check: new head detected, should retry again
      await (queue as any).checkBlockedIssues();

      updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('pending');

      const retryEvents = events.filter((e) => e.type === 'rebase_retry');
      expect(retryEvents.length).toBeGreaterThanOrEqual(1);
    });
  });

  describe('T-005: retry count limit (5) → blocked state', () => {
    it('should mark blocked after 5 retries', async () => {
      const project = projectRepo.create({ name: 'Test', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      issueRepo.setMergeState(issue.id, 'conflict');

      let headCallCount = 0;
      vi.spyOn(queue as any, 'getMasterHead').mockImplementation(async () => {
        headCallCount++;
        return `head-${headCallCount}`;
      });

      // Prevent processNext from running during recovery
      (queue as any).processing = true;
      queue.recoverFromDB();

      // Manually set retryCount to 5 (at the limit)
      const entry = queue.getStatus().find((e: MergeEntry) => e.issueNumber === issue.number);
      entry!.retryCount = 5;

      // At retry limit, should trigger blocked state
      await (queue as any).checkBlockedIssues();

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('blocked');

      const blockedEvent = events.find((e) => e.type === 'merge_blocked');
      expect(blockedEvent).toBeDefined();
      expect(blockedEvent?.payload.issueNumber).toBe(issue.number);
    });
  });

  describe('T-006: manual retry resets retryCount to 0', () => {
    it('should reset retry count on manual retry', async () => {
      const project = projectRepo.create({ name: 'Test', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      issueRepo.setMergeState(issue.id, 'blocked');

      // Prevent processNext from running during recovery
      (queue as any).processing = true;
      queue.recoverFromDB();

      // Verify retry count is 0 after recovery
      let entry = queue.getStatus().find((e: MergeEntry) => e.issueNumber === issue.number);
      expect(entry).toBeDefined();
      expect(entry?.retryCount).toBe(0);

      // Simulate some retries
      entry!.retryCount = 3;

      // Manually retry
      const result = queue.retry(issue.number);
      expect(result).toBe(true);

      const retried = queue.getStatus().find((e: MergeEntry) => e.issueNumber === issue.number);
      expect(retried?.mergeState).toBe('pending');
      expect(retried?.retryCount).toBe(0);
    });
  });

  describe('T-007: file overlap detection in pickNext ordering', () => {
    it('should process non-overlapping issue first when overlapping exists', async () => {
      const project = projectRepo.create({ name: 'Test', path: '/test' });
      const issue1 = issueService.create({ projectId: project.id, title: 'Issue 1' });
      const issue2 = issueService.create({ projectId: project.id, title: 'Issue 2' });

      issueRepo.setMergeState(issue1.id, 'pending');
      issueRepo.setMergeState(issue2.id, 'pending');

      let callIndex = 0;
      vi.spyOn(queue as any, 'getChangedFiles').mockImplementation(async () => {
        callIndex++;
        return callIndex === 1 ? ['src/shared.ts'] : ['src/other.ts'];
      });

      queue.enqueue(project.id, issue1.number);
      queue.enqueue(project.id, issue2.number);

      await new Promise((resolve) => setTimeout(resolve, 50));

      const status = queue.getStatus();
      const firstProcessed = status.find((e: MergeEntry) => e.issueNumber === issue2.number);
      expect(firstProcessed?.mergeState).not.toBe('pending');
    });
  });
});
