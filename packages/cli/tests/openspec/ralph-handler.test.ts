import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { executeRalphTask, type RalphTaskHandlerOptions, type RalphTaskHandlerDeps } from '../../src/openspec/ralph/handler';
import type { RalphLoadedTask } from '../../src/openspec/ralph/loader';
import type { OpenSpecChange } from '../../src/openspec/detector';
import type { StageContext } from '../../src/workflow/stage-context';
import type { Task } from '../../src/openspec/context-assembler';

function createMockStageContext(): StageContext {
  return {
    issue: { id: 'issue-42', number: 42, title: 'Test Issue', body: 'Test body', projectId: 'proj-1' },
    acpOptions: { timeout: 600000 } as any,
    worktreeManager: undefined as any,
    emit: vi.fn(),
    log: vi.fn(),
  };
}

function createMinimalRalphLoadedTask(tempDir: string, taskOverrides: Partial<Task> = {}): {
  loadedTask: RalphLoadedTask;
  change: OpenSpecChange;
} {
  const changeDir = path.join(tempDir, 'openspec', 'changes', 'test');
  fs.mkdirSync(changeDir, { recursive: true });
  fs.mkdirSync(path.join(changeDir, 'session-memories'), { recursive: true });
  fs.writeFileSync(path.join(changeDir, 'proposal.md'), '# Test Proposal');
  fs.writeFileSync(path.join(changeDir, 'design.md'), '# Test Design');
  fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({
    version: 1,
    tasks: [{ id: 'T-001', order: 1, title: 'Test Task', description: 'desc', passes: false, attempts: 0, ...taskOverrides }]
  }));

  const change: OpenSpecChange = {
    changePath: changeDir,
    tasksPath: path.join(changeDir, 'tasks.json'),
    sessionMemoriesPath: path.join(changeDir, 'session-memories'),
    proposalPath: path.join(changeDir, 'proposal.md'),
    designPath: path.join(changeDir, 'design.md'),
    specsPath: path.join(changeDir, 'specs'),
  };

  const task: Task = {
    id: 'T-001',
    title: 'Test Task',
    description: 'desc',
    passes: false,
    attempts: 0,
    order: 1,
    error: null,
    dependsOn: [],
    durations: [],
    ...taskOverrides,
  };

  const loadedTask: RalphLoadedTask = { task, change, totalTasks: 1 };
  return { loadedTask, change };
}

function createDeps(mockRunner: ReturnType<typeof vi.fn>): RalphTaskHandlerDeps {
  return {
    worktreePath: '/tmp',
    acpSessionRunner: mockRunner,
    worktreeManager: undefined,
    observers: [],
    onBeforeKill: undefined,
  };
}

describe('executeRalphTask', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-handler-test-'));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  describe('success path', () => {
    it('returns completed status when session runner returns success', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir);
      const mockRunner = vi.fn().mockResolvedValue({ success: true, text: 'done' });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = {};

      const result = await executeRalphTask(loadedTask, ctx, options, deps);

      expect(result.stageTaskResult.status).toBe('completed');
      expect(result.stageTaskResult.taskId).toBe('T-001');
      expect(result.paused).toBeUndefined();
      const updated = JSON.parse(fs.readFileSync(loadedTask.change.tasksPath, 'utf-8'));
      expect(updated.tasks[0]).toMatchObject({ passes: true, attempts: 1, error: null });
      expect(updated.tasks[0].durations).toHaveLength(1);
    });

    it('returns correct taskId and title in result', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir, { id: 'T-999', title: 'My Special Task' });
      const mockRunner = vi.fn().mockResolvedValue({ success: true, text: 'done' });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = {};

      const result = await executeRalphTask(loadedTask, ctx, options, deps);

      expect(result.stageTaskResult.taskId).toBe('T-999');
      expect(result.stageTaskResult.title).toBe('My Special Task');
    });
  });

  describe('retryable failure path', () => {
    it('retries on ac_not_met failure up to maxRetries', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir);
      let callCount = 0;
      const mockRunner = vi.fn().mockImplementation(() => {
        callCount++;
        if (callCount < 3) {
          return Promise.resolve({ success: false, error: 'acceptance criteria not met' });
        }
        return Promise.resolve({ success: true, text: 'done on retry' });
      });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = { maxRetries: 3 };

      const result = await executeRalphTask(loadedTask, ctx, options, deps);

      expect(result.stageTaskResult.status).toBe('completed');
      expect(callCount).toBe(3);
    });

    it('stops after max retries exhausted for retryable failure', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir);
      const mockRunner = vi.fn().mockResolvedValue({ success: false, error: 'acceptance criteria not met' });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = { maxRetries: 0 };

      const result = await executeRalphTask(loadedTask, ctx, options, deps);

      expect(result.stageTaskResult.status).toBe('failed');
      expect(result.stageTaskResult.attempts).toBe(1);
      const updated = JSON.parse(fs.readFileSync(loadedTask.change.tasksPath, 'utf-8'));
      expect(updated.tasks[0]).toMatchObject({ passes: false, attempts: 1, error: 'acceptance criteria not met' });
      expect(updated.tasks[0].durations).toHaveLength(1);
    });

    it('calls onRetry callback when retrying', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir);
      const onRetry = vi.fn();
      let callCount = 0;
      const mockRunner = vi.fn().mockImplementation(() => {
        callCount++;
        if (callCount < 2) {
          return Promise.resolve({ success: false, error: 'timeout' });
        }
        return Promise.resolve({ success: true, text: 'done' });
      });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = { maxRetries: 3, onRetry };

      await executeRalphTask(loadedTask, ctx, options, deps);

      expect(onRetry).toHaveBeenCalled();
    });
  });

  describe('non-retryable failure path', () => {
    it('does not retry dependency failures', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir);
      let callCount = 0;
      const mockRunner = vi.fn().mockImplementation(() => {
        callCount++;
        return Promise.resolve({ success: false, error: 'cannot find module lodash' });
      });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = { maxRetries: 3 };

      const result = await executeRalphTask(loadedTask, ctx, options, deps);

      expect(result.stageTaskResult.status).toBe('failed');
      expect(callCount).toBe(1);
      expect(result.paused).toBe(true);
    });

    it('returns pause reason for non-retryable failure', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir);
      const mockRunner = vi.fn().mockResolvedValue({ success: false, error: '[SPAWN_FAILED] npm install failed' });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = {};

      const result = await executeRalphTask(loadedTask, ctx, options, deps);

      expect(result.paused).toBe(true);
      expect(result.pauseReason).toContain('dependency');
    });
  });

  describe('timeout_with_wip handling', () => {
    it('captures WIP context when timeout with WIP commit occurs', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir);
      const mockRunner = vi.fn().mockResolvedValue({
        success: false,
        error: 'timeout',
        wipCommitted: true,
        failureKind: 'timeout',
      });
      const mockWorktreeManager = {
        findWipCommit: vi.fn().mockResolvedValue({
          changedFiles: ['src/index.ts'],
          diffStat: '1 file changed',
        }),
      };
      const deps: RalphTaskHandlerDeps = {
        worktreePath: tempDir,
        acpSessionRunner: mockRunner,
        worktreeManager: mockWorktreeManager as any,
        observers: [],
        onBeforeKill: undefined,
      };
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = { maxRetries: 1 };

      const result = await executeRalphTask(loadedTask, ctx, options, deps);

      expect(mockWorktreeManager.findWipCommit).toHaveBeenCalled();
      expect(result.paused).toBe(true);
      expect(result.pauseReason).toContain('timeout_with_wip');
    });
  });

  describe('session_failed handling', () => {
    it('treats session_failed result as a failed task attempt', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir);
      const mockRunner = vi.fn().mockResolvedValue({
        success: false,
        error: 'Session liveness probe timed out',
        failureKind: 'session_failed',
        failureReason: 'probe_timeout',
      });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = { maxRetries: 0 };

      const result = await executeRalphTask(loadedTask, ctx, options, deps);

      expect(result.stageTaskResult.status).toBe('failed');
      expect(result.stageTaskResult.attempts).toBe(1);
    });

    it('does not mark task as passed when session fails', async () => {
      const { loadedTask, change } = createMinimalRalphLoadedTask(tempDir);
      const mockRunner = vi.fn().mockResolvedValue({
        success: false,
        error: 'Session liveness probe timed out',
        failureKind: 'session_failed',
        failureReason: 'probe_timeout',
      });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = { maxRetries: 0 };

      const result = await executeRalphTask(loadedTask, ctx, options, deps);

      expect(result.stageTaskResult.status).toBe('failed');
      expect(result.stageTaskResult.output).toEqual({ error: 'Session liveness probe timed out' });

      const updated = JSON.parse(fs.readFileSync(change.tasksPath, 'utf-8'));
      expect(updated.tasks[0].passes).toBe(false);
    });

    it('returns session_failed category in result', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir);
      const mockRunner = vi.fn().mockResolvedValue({
        success: false,
        error: 'Session liveness probe timed out',
        failureKind: 'session_failed',
        failureReason: 'probe_timeout',
      });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = { maxRetries: 0 };

      const result = await executeRalphTask(loadedTask, ctx, options, deps);

      expect(result.lastCategory).toBe('session_failed');
    });

    it('subjects session_failed to retry policy', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir);
      let callCount = 0;
      const mockRunner = vi.fn().mockImplementation(() => {
        callCount++;
        if (callCount < 2) {
          return Promise.resolve({ success: false, error: 'Session liveness probe timed out', failureKind: 'session_failed' });
        }
        return Promise.resolve({ success: true, text: 'done' });
      });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = { maxRetries: 2 };

      const result = await executeRalphTask(loadedTask, ctx, options, deps);

      expect(result.stageTaskResult.status).toBe('completed');
      expect(callCount).toBe(2);
    });
  });

  describe('emitTaskUpdate callback', () => {
    it('emits started status before execution', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir);
      const emitTaskUpdate = vi.fn();
      const mockRunner = vi.fn().mockResolvedValue({ success: true, text: 'done' });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = { emitTaskUpdate };

      await executeRalphTask(loadedTask, ctx, options, deps);

      expect(emitTaskUpdate).toHaveBeenCalledWith(
        expect.any(String), 'T-001', 'Test Task', expect.any(Number), 1, 'started', expect.any(Number)
      );
    });

    it('emits completed status after success', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir);
      const emitTaskUpdate = vi.fn();
      const mockRunner = vi.fn().mockResolvedValue({ success: true, text: 'done' });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = { emitTaskUpdate };

      await executeRalphTask(loadedTask, ctx, options, deps);

      expect(emitTaskUpdate).toHaveBeenCalledWith(
        expect.any(String), 'T-001', 'Test Task', 1, 1, 'completed', 1
      );
    });

    it('emits failed status after non-retryable failure', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir);
      const emitTaskUpdate = vi.fn();
      const mockRunner = vi.fn().mockResolvedValue({ success: false, error: '[SPAWN_FAILED] npm install failed' });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = { emitTaskUpdate };

      await executeRalphTask(loadedTask, ctx, options, deps);

      expect(emitTaskUpdate).toHaveBeenCalledWith(
        expect.any(String), 'T-001', 'Test Task', 1, 1, 'failed', 1, '[SPAWN_FAILED] npm install failed'
      );
    });

    it('emits retrying status when retrying', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir);
      const emitTaskUpdate = vi.fn();
      let callCount = 0;
      const mockRunner = vi.fn().mockImplementation(() => {
        callCount++;
        if (callCount < 2) {
          return Promise.resolve({ success: false, error: 'timeout' });
        }
        return Promise.resolve({ success: true, text: 'done' });
      });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = { emitTaskUpdate, maxRetries: 3 };

      await executeRalphTask(loadedTask, ctx, options, deps);

      expect(emitTaskUpdate).toHaveBeenCalledWith(
        expect.any(String), 'T-001', 'Test Task', expect.any(Number), 1, 'retrying', expect.any(Number)
      );
    });

    it('marks terminal handler results as already reported', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir);
      const mockRunner = vi.fn().mockResolvedValue({ success: true, text: 'done' });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();

      const result = await executeRalphTask(loadedTask, ctx, {}, deps);

      expect(result.stageTaskResult.alreadyReported).toBe(true);
    });
  });

  describe('failure learning persistence', () => {
    it('stores failure learning on task failure', async () => {
      const { loadedTask, change } = createMinimalRalphLoadedTask(tempDir);
      const mockRunner = vi.fn().mockResolvedValue({
        success: false,
        error: 'acceptance criteria not met',
      });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = {};

      await executeRalphTask(loadedTask, ctx, options, deps);

      const learningPath = path.join(change.sessionMemoriesPath, 'T-001.json');
      expect(fs.existsSync(learningPath)).toBe(true);
      const learning = JSON.parse(fs.readFileSync(learningPath, 'utf-8'));
      expect(learning.task_id).toBe('T-001');
      expect(learning.failure_category).toBe('ac_not_met');
    });
  });

  describe('onlyTaskId execution', () => {
    it('executes single task when onlyTaskId is set', async () => {
      const { loadedTask } = createMinimalRalphLoadedTask(tempDir, { id: 'T-001', order: 1 });
      const mockRunner = vi.fn().mockResolvedValue({ success: true, text: 'done' });
      const deps = createDeps(mockRunner);
      const ctx = createMockStageContext();
      const options: RalphTaskHandlerOptions = { issueId: '42' };

      const result = await executeRalphTask(loadedTask, ctx, options, deps);

      expect(result.stageTaskResult.status).toBe('completed');
      expect(mockRunner).toHaveBeenCalledTimes(1);
    });
  });
});
