import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { IssueService } from '../src/services/issue-service';
import { EventBus } from '../src/services/event-bus';
import { MergeQueue, MergeEntry } from '../src/git/merge-queue';
import { WorktreeManager } from '../src/git/worktree-manager';
import { MergeState } from '../src/types';

vi.mock('child_process', async (importOriginal) => {
  const actual = await importOriginal<typeof import('child_process')>();
  return {
    ...actual,
    execFile: vi.fn(),
  };
});

import { execFile } from 'child_process';

const execFileMock = vi.mocked(execFile);

const WORKTREE_PATH = '/tmp/test-project/worktrees/issue-1';

function createMockWorktreeManager() {
  return {
    canFastForward: vi.fn().mockResolvedValue(true),
    rebaseOntoMaster: vi.fn().mockResolvedValue({ success: true, conflicts: [] }),
    rebaseContinue: vi.fn().mockResolvedValue({ success: true, conflicts: [] }),
    abortRebase: vi.fn().mockResolvedValue(undefined),
    mergeBack: vi.fn().mockResolvedValue({ success: true, message: 'Merged' }),
    remove: vi.fn().mockResolvedValue(undefined),
    exists: vi.fn().mockReturnValue(true),
    create: vi.fn().mockResolvedValue(WORKTREE_PATH),
    getPath: vi.fn().mockReturnValue(WORKTREE_PATH),
  } as unknown as WorktreeManager;
}

const resolveConflictsMock = vi.fn().mockResolvedValue({ success: true });
const fixBuildErrorsMock = vi.fn().mockResolvedValue({ success: true });

function createMergeQueueDeps(overrides?: Partial<{
  worktreeManager: WorktreeManager;
  eventBus: EventBus;
  issueRepo: IssueRepo;
  getProjectPath: (projectId: string) => { path: string; name: string; baseBranch: string } | null;
  resolveConflicts: typeof resolveConflictsMock;
  fixBuildErrors: typeof fixBuildErrorsMock;
  postMergeFinalizer: { finalize: ReturnType<typeof vi.fn> };
}>) {
  return overrides as any;
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
    resolveConflictsMock.mockReset().mockResolvedValue({ success: true });
    fixBuildErrorsMock.mockReset().mockResolvedValue({ success: true });
    execFileMock.mockReset();
    const gitDir = path.join(PROJECT_PATH, '.git');
    fs.mkdirSync(gitDir, { recursive: true });
    fs.writeFileSync(path.join(gitDir, 'mohist-last-fetch'), Date.now().toString(), 'utf-8');
  });

  afterEach(() => {
    db.close();
    fs.rmSync(PROJECT_PATH, { recursive: true, force: true });
  });

  function setupProject() {
    return projectRepo.create({ name: PROJECT_NAME, path: PROJECT_PATH, baseBranch: BASE_BRANCH });
  }

  function createQueue(projectId: string, extraDeps?: Record<string, any>) {
    return new MergeQueue({
      worktreeManager,
      eventBus,
      issueRepo,
      getProjectPath: (pid: string) => {
        if (pid !== projectId) return null;
        return { path: PROJECT_PATH, name: PROJECT_NAME, baseBranch: BASE_BRANCH };
      },
      resolveConflicts: resolveConflictsMock,
      fixBuildErrors: fixBuildErrorsMock,
      postMergeFinalizer: {
        finalize: vi.fn().mockImplementation(async (issue) => {
          issueRepo.setMergeState(issue.id, MergeState.Merged);
          return { success: true, healthGateResult: { passed: true, enabled: true } };
        }),
      } as any,
      getMergeMetadata: vi.fn().mockImplementation(async (_projectId: string, issueNumber: number) => {
        const issue = issueRepo.findByNumber(projectId, issueNumber);
        if (!issue) return undefined;
        return {
          issueNumber,
          issueTitle: issue.title,
        };
      }),
      ...extraDeps,
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
    });

    it('should ignore duplicate enqueue for same issue', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });

      worktreeManager.canFastForward = vi.fn().mockImplementation(async () => {
        await new Promise((r) => setTimeout(r, 100));
        return true;
      });

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
    it('does not mark merged or emit completion when postMerge finalizer fails', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const finalizer = {
        finalize: vi.fn().mockResolvedValue({ success: false, error: 'postMerge failed' }),
      };
      const getMergeMetadata = vi.fn().mockResolvedValue({
        issueNumber: issue.number,
        issueTitle: issue.title,
      });
      const queue = new MergeQueue({
        worktreeManager,
        eventBus,
        issueRepo,
        getProjectPath: (pid: string) => {
          if (pid !== project.id) return null;
          return { path: PROJECT_PATH, name: PROJECT_NAME, baseBranch: BASE_BRANCH };
        },
        resolveConflicts: resolveConflictsMock,
        fixBuildErrors: fixBuildErrorsMock,
        postMergeFinalizer: finalizer as any,
        getMergeMetadata,
      });

      const completedEvents: any[] = [];
      const failedEvents: any[] = [];
      eventBus.on('merge_completed', (data) => completedEvents.push(data));
      eventBus.on('merge_failed', (data) => failedEvents.push(data));

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      expect(finalizer.finalize).toHaveBeenCalled();
      expect(completedEvents).toHaveLength(0);
      expect(failedEvents).toHaveLength(1);
      expect(issueRepo.findById(issue.id)?.mergeState).toBe(MergeState.BuildFailed);
    });

    it('fails closed without completion when postMerge finalizer is missing', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = new MergeQueue({
        worktreeManager,
        eventBus,
        issueRepo,
        getProjectPath: (pid: string) => {
          if (pid !== project.id) return null;
          return { path: PROJECT_PATH, name: PROJECT_NAME, baseBranch: BASE_BRANCH };
        },
        resolveConflicts: resolveConflictsMock,
        fixBuildErrors: fixBuildErrorsMock,
        getMergeMetadata: vi.fn().mockResolvedValue({
          issueNumber: issue.number,
          issueTitle: issue.title,
        }),
      });

      const completedEvents: any[] = [];
      const failedEvents: any[] = [];
      eventBus.on('merge_completed', (data) => completedEvents.push(data));
      eventBus.on('merge_failed', (data) => failedEvents.push(data));

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      expect(completedEvents).toHaveLength(0);
      expect(failedEvents).toHaveLength(1);
      expect(failedEvents[0].reason).toBe(MergeState.BuildFailed);
      expect(queue.getStatus()).toHaveLength(1);
      expect(issueRepo.findById(issue.id)?.stage).not.toBe('done');
      expect(issueRepo.findById(issue.id)?.status).not.toBe('completed');
      expect(issueRepo.findById(issue.id)?.mergeState).toBe(MergeState.BuildFailed);
    });

    it('should squash merge when canFastForward is true, skipping rebase', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      worktreeManager.canFastForward = vi.fn().mockResolvedValue(true);
      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });

      const completedEvents: any[] = [];
      eventBus.on('merge_completed', (data) => completedEvents.push(data));

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      expect(worktreeManager.canFastForward).toHaveBeenCalledWith(
        PROJECT_PATH, PROJECT_NAME, issue.number, BASE_BRANCH,
      );
      expect(worktreeManager.rebaseOntoMaster).not.toHaveBeenCalled();
      expect(worktreeManager.mergeBack).toHaveBeenCalledWith(
        PROJECT_PATH,
        PROJECT_NAME,
        issue.number,
        BASE_BRANCH,
        expect.objectContaining({
          issueNumber: issue.number,
          issueTitle: issue.title,
        }),
      );
      expect(completedEvents).toHaveLength(1);
      expect(issueRepo.findById(issue.id)?.mergeState).toBe('merged');
    });

    it('should pass getMergeMetadata result to mergeBack', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });

      const metadata = {
        issueNumber: issue.number,
        issueTitle: issue.title,
        tasks: [{ id: 'T-001', title: 'Do the thing' }],
      };

      const queue = createQueue(project.id, {
        getMergeMetadata: vi.fn().mockResolvedValue(metadata),
      });

      worktreeManager.canFastForward = vi.fn().mockResolvedValue(true);
      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      expect(worktreeManager.mergeBack).toHaveBeenCalledWith(
        PROJECT_PATH, PROJECT_NAME, issue.number, BASE_BRANCH, metadata,
      );
      expect(issueRepo.findById(issue.id)?.mergeState).toBe('merged');
    });

    it('should fall back to issue title when merge metadata is unavailable', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id, {
        getMergeMetadata: vi.fn().mockResolvedValue(undefined),
      });

      const failedEvents: any[] = [];
      eventBus.on('merge_failed', (data) => failedEvents.push(data));

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      expect(worktreeManager.mergeBack).toHaveBeenCalledWith(
        PROJECT_PATH,
        PROJECT_NAME,
        issue.number,
        BASE_BRANCH,
        {
          issueNumber: issue.number,
          issueTitle: issue.title,
        },
      );
      expect(issueRepo.findById(issue.id)?.mergeState).toBe('merged');
      expect(failedEvents).toHaveLength(0);
    });

    it('should rebase then squash merge when canFastForward is false', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      let ffCallCount = 0;
      worktreeManager.canFastForward = vi.fn().mockImplementation(async () => {
        ffCallCount++;
        return ffCallCount > 1;
      });
      worktreeManager.rebaseOntoMaster = vi.fn().mockResolvedValue({ success: true, conflicts: [] });
      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });

      const completedEvents: any[] = [];
      eventBus.on('merge_completed', (data) => completedEvents.push(data));

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      expect(worktreeManager.rebaseOntoMaster).toHaveBeenCalledWith(
        PROJECT_PATH, PROJECT_NAME, issue.number, BASE_BRANCH,
        { abortOnConflict: false },
      );
      expect(worktreeManager.mergeBack).toHaveBeenCalled();
      expect(completedEvents).toHaveLength(1);
      expect(issueRepo.findById(issue.id)?.mergeState).toBe('merged');
    });

    it('should process items serially in FIFO order', async () => {
      const project = setupProject();
      const issue1 = issueService.create({ projectId: project.id, title: 'Issue 1' });
      const issue2 = issueService.create({ projectId: project.id, title: 'Issue 2' });

      const order: number[] = [];
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

  describe('rebase conflict → agent resolution', () => {
    it('should resolve conflicts via agent then continue rebase', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      let ffCallCount = 0;
      worktreeManager.canFastForward = vi.fn().mockImplementation(async () => {
        ffCallCount++;
        return ffCallCount > 1;
      });
      worktreeManager.rebaseOntoMaster = vi.fn().mockResolvedValue({
        success: false,
        conflicts: ['src/foo.ts', 'src/bar.ts'],
      });
      (worktreeManager as any).isRebaseInProgress = vi.fn().mockResolvedValue(false);
      resolveConflictsMock.mockResolvedValue({ success: true });
      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });

      const completedEvents: any[] = [];
      eventBus.on('merge_completed', (data) => completedEvents.push(data));

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      expect(resolveConflictsMock).toHaveBeenCalledWith(
        expect.objectContaining({ issueNumber: issue.number }),
        WORKTREE_PATH,
        ['src/foo.ts', 'src/bar.ts'],
      );
      expect(completedEvents).toHaveLength(1);
      expect(issueRepo.findById(issue.id)?.mergeState).toBe('merged');
    });

    it('should set conflict state when agent resolution fails', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      worktreeManager.canFastForward = vi.fn().mockResolvedValue(false);
      worktreeManager.rebaseOntoMaster = vi.fn().mockResolvedValue({
        success: false,
        conflicts: ['src/foo.ts'],
      });
      resolveConflictsMock.mockResolvedValue({ success: false, error: 'Agent failed' });

      const failedEvents: any[] = [];
      eventBus.on('merge_failed', (data) => failedEvents.push(data));

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      expect(resolveConflictsMock).toHaveBeenCalled();
      expect(worktreeManager.abortRebase).toHaveBeenCalled();
      expect(issueRepo.findById(issue.id)?.mergeState).toBe('conflict');
      expect(failedEvents).toHaveLength(1);
    });

    it('should set conflict state when rebase continue fails after agent resolution', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      worktreeManager.canFastForward = vi.fn().mockResolvedValue(false);
      worktreeManager.rebaseOntoMaster = vi.fn().mockResolvedValue({
        success: false,
        conflicts: ['src/foo.ts'],
      });
      resolveConflictsMock.mockResolvedValue({ success: true });
      (worktreeManager as any).isRebaseInProgress = vi.fn().mockResolvedValue(true);

      const failedEvents: any[] = [];
      eventBus.on('merge_failed', (data) => failedEvents.push(data));

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      expect(worktreeManager.abortRebase).toHaveBeenCalled();
      expect(issueRepo.findById(issue.id)?.mergeState).toBe('conflict');
      expect(failedEvents).toHaveLength(1);
    });
  });

  describe('build verification (only after rebase)', () => {
    it('should run npm run build in worktree after rebase and set merged on success', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      let ffCallCount = 0;
      worktreeManager.canFastForward = vi.fn().mockImplementation(async () => {
        ffCallCount++;
        return ffCallCount > 1;
      });
      worktreeManager.rebaseOntoMaster = vi.fn().mockResolvedValue({ success: true, conflicts: [] });
      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      const buildCalls = execFileMock.mock.calls.filter(
        (c: any) => c[0] === 'npm' && c[1]?.[0] === 'run' && c[1]?.[1] === 'build',
      );
      expect(buildCalls.length).toBe(1);
      expect(buildCalls[0][2]?.cwd).toBe(path.join(WORKTREE_PATH, 'packages', 'cli'));

      expect(issueRepo.findById(issue.id)?.mergeState).toBe('merged');
    });

    it('should NOT run build verification when canFF is true (no rebase)', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      worktreeManager.canFastForward = vi.fn().mockResolvedValue(true);

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      const buildCalls = execFileMock.mock.calls.filter(
        (c: any) => c[0] === 'npm' && c[1]?.[0] === 'run' && c[1]?.[1] === 'build',
      );
      expect(buildCalls.length).toBe(0);

      expect(issueRepo.findById(issue.id)?.mergeState).toBe('merged');
    });

    it('should set build-failed when build fails after rebase and agent fix fails', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      let ffCallCount = 0;
      worktreeManager.canFastForward = vi.fn().mockImplementation(async () => {
        ffCallCount++;
        return ffCallCount > 1;
      });
      worktreeManager.rebaseOntoMaster = vi.fn().mockResolvedValue({ success: true, conflicts: [] });

      let buildCalled = false;
      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        if (cmd === 'npm' && args?.[1] === 'build') {
          buildCalled = true;
          cb?.(new Error('Build failed') as any, '', 'error output');
          return undefined as any;
        }
        cb?.(null, '', '');
        return undefined as any;
      });
      fixBuildErrorsMock.mockResolvedValue({ success: false, error: 'Agent could not fix build' });

      const failedEvents: any[] = [];
      eventBus.on('merge_failed', (data) => failedEvents.push(data));

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      expect(buildCalled).toBe(true);
      expect(fixBuildErrorsMock).toHaveBeenCalled();
      expect(issueRepo.findById(issue.id)?.mergeState).toBe('build-failed');
      expect(failedEvents).toHaveLength(1);
      expect(failedEvents[0].reason).toBe('build-failed');
    });

    it('should fix build errors via agent then succeed', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      let ffCallCount = 0;
      worktreeManager.canFastForward = vi.fn().mockImplementation(async () => {
        ffCallCount++;
        return ffCallCount > 1;
      });
      worktreeManager.rebaseOntoMaster = vi.fn().mockResolvedValue({ success: true, conflicts: [] });

      let buildCallCount = 0;
      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        if (cmd === 'npm' && args?.[1] === 'build') {
          buildCallCount++;
          if (buildCallCount === 1) {
            cb?.(new Error('Build failed') as any, '', 'error output');
            return undefined as any;
          }
        }
        cb?.(null, '', '');
        return undefined as any;
      });
      fixBuildErrorsMock.mockResolvedValue({ success: true });

      const completedEvents: any[] = [];
      eventBus.on('merge_completed', (data) => completedEvents.push(data));

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      expect(fixBuildErrorsMock).toHaveBeenCalledWith(
        expect.objectContaining({ issueNumber: issue.number }),
        WORKTREE_PATH,
        expect.any(String),
      );
      expect(buildCallCount).toBe(2);
      expect(completedEvents).toHaveLength(1);
      expect(issueRepo.findById(issue.id)?.mergeState).toBe('merged');
    });
  });

  describe('merge conflict', () => {
    it('should set conflict state when mergeBack fails with conflict', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      setupMockExecFile();

      worktreeManager.mergeBack = vi.fn().mockResolvedValue({
        success: false,
        message: 'Merge conflict for issue #1: CONFLICT (content): Merge conflict in src/foo.ts',
        targetBranch: 'main',
        baseSha: 'base-sha',
        candidateHeadSha: 'candidate-sha',
      });

      const failedEvents: any[] = [];
      eventBus.on('merge_failed', (data) => failedEvents.push(data));

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('conflict');

      expect(failedEvents).toHaveLength(1);
      expect(failedEvents[0].reason).toBe('conflict');
    });

    it('should set build-failed when mergeBack fails without conflict', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      setupMockExecFile();

      worktreeManager.mergeBack = vi.fn().mockResolvedValue({
        success: false,
        message: 'Failed to checkout main: some error',
        targetBranch: 'main',
        baseSha: 'base-sha',
        candidateHeadSha: 'candidate-sha',
      });

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('build-failed');
    });
  });

  describe('retry', () => {
    it('should retry from build-failed state', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
      const queue = createQueue(project.id);

      let callCount = 0;
      worktreeManager.mergeBack = vi.fn().mockImplementation(async () => {
        callCount++;
        if (callCount === 1) {
          return {
            success: false,
            message: 'Build failed',
            targetBranch: 'main',
            baseSha: 'base-sha',
            candidateHeadSha: 'candidate-sha',
          };
        }
        return { success: true, message: 'Merged' };
      });

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });

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

      let callCount = 0;
      worktreeManager.mergeBack = vi.fn().mockImplementation(async () => {
        callCount++;
        if (callCount === 1) {
          return {
            success: false,
            message: 'Merge conflict: CONFLICT in file.ts',
            targetBranch: 'main',
            baseSha: 'base-sha',
            candidateHeadSha: 'candidate-sha',
          };
        }
        return { success: true, message: 'Merged' };
      });

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      expect(issueRepo.findById(issue.id)?.mergeState).toBe('conflict');

      const retried = queue.retry(issue.number);
      expect(retried).toBe(true);

      await waitForQueueToSettle(queue);

      expect(issueRepo.findById(issue.id)?.mergeState).toBe('merged');
    });

    it('should return false for non-retryable states', () => {
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

      worktreeManager.mergeBack = vi.fn().mockImplementation(async () => {
        await new Promise((r) => setTimeout(r, 50));
        return { success: true, message: 'Merged' };
      });

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });

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

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });

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

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });

      const queue = createQueue(project.id);

      queue.recoverFromDB();

      await waitForQueueToSettle(queue);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('merged');

      expect(worktreeManager.mergeBack).toHaveBeenCalledTimes(1);
    });

    it('should re-enqueue issues with resolving state (reset to pending)', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });

      issueRepo.setMergeState(issue.id, 'resolving');

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });

      const queue = createQueue(project.id);

      queue.recoverFromDB();

      await waitForQueueToSettle(queue);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('merged');
    });

    it('should recover multiple issues and process them serially', async () => {
      const project = setupProject();
      const issue1 = issueService.create({ projectId: project.id, title: 'Issue 1' });
      const issue2 = issueService.create({ projectId: project.id, title: 'Issue 2' });

      issueRepo.setMergeState(issue1.id, 'pending');
      issueRepo.setMergeState(issue2.id, 'merging');

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });

      const queue = createQueue(project.id);

      queue.recoverFromDB();

      await waitForQueueToSettle(queue);

      expect(issueRepo.findById(issue1.id)?.mergeState).toBe('merged');
      expect(issueRepo.findById(issue2.id)?.mergeState).toBe('merged');
      expect(worktreeManager.mergeBack).toHaveBeenCalledTimes(2);
    });

    it('should not recover issues that are already in queue', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });

      worktreeManager.mergeBack = vi.fn().mockImplementation(async () => {
        await new Promise((r) => setTimeout(r, 200));
        return { success: true, message: 'Merged' };
      });

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });

      const queue = createQueue(project.id);
      queue.enqueue(project.id, issue.number);

      queue.recoverFromDB();

      await waitForQueueToSettle(queue);

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
    it('should fail with build-failed when project path not found', async () => {
      const project = setupProject();
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });

      const queue = new MergeQueue({
        worktreeManager,
        eventBus,
        issueRepo,
        getProjectPath: () => null,
        resolveConflicts: resolveConflictsMock,
        fixBuildErrors: fixBuildErrorsMock,
        getMergeMetadata: vi.fn().mockResolvedValue({
          issueNumber: issue.number,
          issueTitle: issue.title,
        }),
      });

      const failedEvents: any[] = [];
      eventBus.on('merge_failed', (data) => failedEvents.push(data));

      queue.enqueue(project.id, issue.number);
      await waitForQueueToSettle(queue);

      const updated = issueRepo.findById(issue.id);
      expect(updated?.mergeState).toBe('build-failed');
      expect(failedEvents).toHaveLength(1);
    });
  });

  describe('merge_queued event position', () => {
    it('should report correct queue position', async () => {
      const project = setupProject();
      const issue1 = issueService.create({ projectId: project.id, title: 'Issue 1' });
      const issue2 = issueService.create({ projectId: project.id, title: 'Issue 2' });

      worktreeManager.mergeBack = vi.fn().mockImplementation(async () => {
        await new Promise((r) => setTimeout(r, 100));
        return { success: true, message: 'Merged' };
      });

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });

      const queue = createQueue(project.id);
      const positions: number[] = [];
      eventBus.on('merge_queued', (data) => {
        positions.push(data.position);
      });

      queue.enqueue(project.id, issue1.number);
      queue.enqueue(project.id, issue2.number);

      await waitForQueueToSettle(queue);

      expect(positions[0]).toBe(1);
      expect(positions[1]).toBe(1);
    });
  });
});
