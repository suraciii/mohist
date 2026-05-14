import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { runRalphLoop, setAcpSessionRunner, resetAcpSessionRunner, validateTaskDependencies, findNextPendingTask } from '../../src/openspec/ralph-executor';
import type { OpenSpecChange } from '../../src/openspec/detector';

describe('Ralph loop compatibility', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-loop-comp-test-'));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
    resetAcpSessionRunner();
  });

  function createChangeWithTasks(tasks: any[]): OpenSpecChange {
    const changeDir = path.join(tempDir, 'openspec', 'changes', '42-test');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.mkdirSync(path.join(changeDir, 'session-memories'), { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks }));
    fs.writeFileSync(path.join(changeDir, 'proposal.md'), '# Test');
    fs.writeFileSync(path.join(changeDir, 'design.md'), '# Design');
    return {
      changePath: changeDir,
      tasksPath: path.join(changeDir, 'tasks.json'),
      sessionMemoriesPath: path.join(changeDir, 'session-memories'),
      proposalPath: path.join(changeDir, 'proposal.md'),
      designPath: path.join(changeDir, 'design.md'),
      specsPath: path.join(changeDir, 'specs'),
    };
  }

  describe('onlyTaskId', () => {
    it('executes only the specified task and returns single result', async () => {
      setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

      const change = createChangeWithTasks([
        { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: false, attempts: 0 },
        { id: 'T-002', order: 2, title: 'Second Task', description: 'desc', passes: false, attempts: 0 },
      ]);

      const result = await runRalphLoop(change, {
        worktreePath: tempDir,
        projectPath: tempDir,
        issueId: 'issue-42',
      }, { onlyTaskId: 'T-001', maxRetries: 0 });

      expect(result.total).toBe(1);
      expect(result.completed).toBe(1);
      expect(result.taskResults).toHaveLength(1);
      expect(result.taskResults[0].taskId).toBe('T-001');
      expect(result.taskResults[0].status).toBe('completed');
    });

    it('fails when onlyTaskId does not exist', async () => {
      setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

      const change = createChangeWithTasks([
        { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: false, attempts: 0 },
      ]);

      const result = await runRalphLoop(change, {
        worktreePath: tempDir,
        projectPath: tempDir,
        issueId: 'issue-42',
      }, { onlyTaskId: 'T-999', maxRetries: 0 });

      expect(result.success).toBe(false);
      expect(result.failed).toBe(1);
      expect(result.taskResults[0].error).toContain('T-999');
    });

    it('fails when onlyTaskId is already passed', async () => {
      setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

      const change = createChangeWithTasks([
        { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: true, attempts: 1 },
      ]);

      const result = await runRalphLoop(change, {
        worktreePath: tempDir,
        projectPath: tempDir,
        issueId: 'issue-42',
      }, { onlyTaskId: 'T-001', maxRetries: 0 });

      expect(result.success).toBe(false);
      expect(result.failed).toBe(1);
      expect(result.taskResults[0].error).toContain('already passed');
    });

    it('fails when onlyTaskId is blocked by dependencies', async () => {
      setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

      const change = createChangeWithTasks([
        { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: false, attempts: 0 },
        { id: 'T-002', order: 2, title: 'Second Task', description: 'desc', passes: false, attempts: 0, dependsOn: ['T-001'] },
      ]);

      const result = await runRalphLoop(change, {
        worktreePath: tempDir,
        projectPath: tempDir,
        issueId: 'issue-42',
      }, { onlyTaskId: 'T-002', maxRetries: 0 });

      expect(result.success).toBe(false);
      expect(result.failed).toBe(1);
      expect(result.taskResults[0].error).toContain('not ready');
      expect(result.taskResults[0].error).toContain('T-001');
    });

    it('allows aggregate single-task execution when task file progress is ignored', async () => {
      setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

      const change = createChangeWithTasks([
        { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: false, attempts: 0 },
        { id: 'T-002', order: 2, title: 'Second Task', description: 'desc', passes: false, attempts: 0 },
      ]);

      const result = await runRalphLoop(change, {
        worktreePath: tempDir,
        projectPath: tempDir,
        issueId: 'issue-42',
      }, { onlyTaskId: 'T-002', maxRetries: 0, ignoreTaskFileProgress: true });

      expect(result.success).toBe(true);
      expect(result.completed).toBe(1);
      expect(result.taskResults).toHaveLength(1);
      expect(result.taskResults[0]).toMatchObject({ taskId: 'T-002', status: 'completed' });
    });

    it('persists stage task duration from handler result for single-task execution', async () => {
      setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

      const change = createChangeWithTasks([
        { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: false, attempts: 0 },
      ]);
      const stageExecutionRepo = { appendTaskResult: vi.fn() } as any;

      await runRalphLoop(change, {
        worktreePath: tempDir,
        projectPath: tempDir,
        issueId: 'issue-42',
        stageExecutionId: 'stage-1',
        stageExecutionRepo,
      }, { onlyTaskId: 'T-001', maxRetries: 0, ignoreTaskFileProgress: true });

      expect(stageExecutionRepo.appendTaskResult).toHaveBeenCalledWith(
        'stage-1',
        expect.objectContaining({
          taskId: 'T-001',
          status: 'completed',
          duration: expect.any(Number),
        }),
      );
      expect(stageExecutionRepo.appendTaskResult.mock.calls[0][1].duration).toBeGreaterThan(0);
    });

    it('updates tasks.json only for the specified task', async () => {
      setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

      const change = createChangeWithTasks([
        { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: false, attempts: 0 },
        { id: 'T-002', order: 2, title: 'Second Task', description: 'desc', passes: false, attempts: 0 },
      ]);

      await runRalphLoop(change, {
        worktreePath: tempDir,
        projectPath: tempDir,
        issueId: 'issue-42',
      }, { onlyTaskId: 'T-001', maxRetries: 0 });

      const updated = JSON.parse(fs.readFileSync(change.tasksPath, 'utf-8'));
      expect(updated.tasks[0].passes).toBe(true);
      expect(updated.tasks[1].passes).toBe(false);
    });

    it('preserves handler failure category for single-task execution', async () => {
      setAcpSessionRunner(vi.fn().mockResolvedValue({
        success: false,
        error: 'Session liveness probe timed out',
        failureKind: 'session_failed',
      }));

      const change = createChangeWithTasks([
        { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: false, attempts: 0 },
      ]);
      const workflowLogRepo = { insert: vi.fn() } as any;

      await runRalphLoop(change, {
        worktreePath: tempDir,
        projectPath: tempDir,
        workflowLogRepo,
        issueId: 'issue-42',
        issueNumber: 42,
      }, { onlyTaskId: 'T-001', maxRetries: 0 });

      expect(workflowLogRepo.insert).toHaveBeenCalledWith(
        'issue-42',
        null,
        'task_failed',
        expect.objectContaining({ taskId: 'T-001', category: 'session_failed' }),
      );
    });
  });

  describe('checkpoint recovery (skipTaskIds)', () => {
    it('restores tasks from checkpoint and skips execution', async () => {
      setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

      const change = createChangeWithTasks([
        { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: false, attempts: 0 },
        { id: 'T-002', order: 2, title: 'Second Task', description: 'desc', passes: false, attempts: 0 },
      ]);

      const onTaskStart = vi.fn();
      const result = await runRalphLoop(change, {
        worktreePath: tempDir,
        projectPath: tempDir,
        onTaskStart,
      }, { skipTaskIds: ['T-001', 'T-002'], maxRetries: 0 });

      expect(result.completed).toBe(2);
      expect(result.total).toBe(2);
      expect(result.success).toBe(true);
      expect(onTaskStart).not.toHaveBeenCalled();

      const updated = JSON.parse(fs.readFileSync(change.tasksPath, 'utf-8'));
      expect(updated.tasks.every((t: any) => t.passes === true)).toBe(true);
    });

    it('continues execution for tasks not in skipTaskIds', async () => {
      setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

      const change = createChangeWithTasks([
        { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: false, attempts: 0 },
        { id: 'T-002', order: 2, title: 'Second Task', description: 'desc', passes: false, attempts: 0 },
      ]);

      const onTaskStart = vi.fn();
      const result = await runRalphLoop(change, {
        worktreePath: tempDir,
        projectPath: tempDir,
        onTaskStart,
      }, { skipTaskIds: ['T-001'], maxRetries: 0 });

      expect(result.completed).toBe(1);
      expect(result.total).toBe(2);
      expect(result.success).toBe(true);
      expect(onTaskStart).toHaveBeenCalledTimes(1);
    });
  });

  describe('validation failure', () => {
    it('fails loop when task depends on non-existent task', async () => {
      setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

      const change = createChangeWithTasks([
        { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-999'] },
      ]);

      const result = await runRalphLoop(change, {
        worktreePath: tempDir,
        projectPath: tempDir,
        issueId: 'issue-42',
      });

      expect(result.success).toBe(false);
      expect(result.failed).toBe(1);
      expect(result.completed).toBe(0);
      expect(result.pauseReason).toContain('validation failed');
    });

    it('fails loop when circular dependency detected', async () => {
      setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

      const change = createChangeWithTasks([
        { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-002'] },
        { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
      ]);

      const result = await runRalphLoop(change, {
        worktreePath: tempDir,
        projectPath: tempDir,
        issueId: 'issue-42',
      });

      expect(result.success).toBe(false);
      expect(result.pauseReason).toContain('validation failed');
    });
  });

  describe('deadlock handling', () => {
    it('fails loop when validation fails (circular dependency)', async () => {
      setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

      const change = createChangeWithTasks([
        { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-002'] },
        { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
      ]);

      const result = await runRalphLoop(change, {
        worktreePath: tempDir,
        projectPath: tempDir,
        issueId: 'issue-42',
      });

      expect(result.success).toBe(false);
      expect(result.pauseReason).toContain('validation failed');
    });
  });
});

describe('validateTaskDependencies', () => {
  it('returns valid for well-formed dependency chain', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: [] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
      { id: 'T-003', order: 3, title: 'C', description: 'd', passes: false, attempts: 0, dependsOn: ['T-002'] },
    ];
    const result = validateTaskDependencies(tasks);
    expect(result.valid).toBe(true);
    expect(result.errors).toHaveLength(0);
  });

  it('returns invalid for missing dependency', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-999'] },
    ];
    const result = validateTaskDependencies(tasks);
    expect(result.valid).toBe(false);
    expect(result.errors.some(e => e.includes('T-999'))).toBe(true);
  });

  it('returns invalid for circular dependency', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-002'] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
    ];
    const result = validateTaskDependencies(tasks);
    expect(result.valid).toBe(false);
    expect(result.errors.some(e => e.toLowerCase().includes('circular'))).toBe(true);
  });

  it('returns invalid when dependency has higher order', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-002'] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: [] },
    ];
    const result = validateTaskDependencies(tasks);
    expect(result.valid).toBe(false);
    expect(result.errors.some(e => e.includes('lower or equal order'))).toBe(true);
  });
});

describe('findNextPendingTask', () => {
  it('returns first pending task by order', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: true, attempts: 1, dependsOn: [] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: [] },
      { id: 'T-003', order: 3, title: 'C', description: 'd', passes: false, attempts: 0, dependsOn: [] },
    ];
    const next = findNextPendingTask(tasks);
    expect(next?.id).toBe('T-002');
  });

  it('returns null when all tasks pass', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: true, attempts: 1, dependsOn: [] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: true, attempts: 1, dependsOn: [] },
    ];
    const next = findNextPendingTask(tasks);
    expect(next).toBeNull();
  });

  it('returns null when all pending tasks are blocked (deadlock)', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-002'] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
    ];
    const next = findNextPendingTask(tasks);
    expect(next).toBeNull();
  });

  it('skips task with unmet dependencies', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: [] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
      { id: 'T-003', order: 3, title: 'C', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001', 'T-002'] },
    ];
    const next = findNextPendingTask(tasks);
    expect(next?.id).toBe('T-001');
  });
});
