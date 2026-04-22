import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import type { OpenSpecChange } from '../src/openspec/detector';
import { setAcpSessionRunner, resetAcpSessionRunner, runRalphLoop } from '../src/openspec/ralph-executor';

describe('Ralph executor logging', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-ralph-log-test-'));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
    resetAcpSessionRunner();
  });

  function createMinimalChange(): OpenSpecChange {
    const changeDir = path.join(tempDir, 'openspec', 'changes', '42-test');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.mkdirSync(path.join(changeDir, 'session-memories'), { recursive: true });

    const tasksFile = {
      version: 1,
      tasks: [
        { id: 'T-001', order: 1, title: 'First Task', description: 'Do first thing', passes: false, attempts: 0 },
      ],
    };
    fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify(tasksFile));
    fs.writeFileSync(path.join(changeDir, 'proposal.md'), '# Test Proposal');
    fs.writeFileSync(path.join(changeDir, 'design.md'), '# Test Design');

    return {
      changePath: changeDir,
      tasksPath: path.join(changeDir, 'tasks.json'),
      sessionMemoriesPath: path.join(changeDir, 'session-memories'),
      proposalPath: path.join(changeDir, 'proposal.md'),
      designPath: path.join(changeDir, 'design.md'),
      specsPath: path.join(changeDir, 'specs'),
    };
  }

  it('should write task_started and task_completed to workflow_log on success', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({
      text: 'done',
      success: true,
    }));

    const change = createMinimalChange();
    const workflowLogRepo = { insert: vi.fn() } as any;

    await runRalphLoop(change, {
      worktreePath: tempDir,
      projectPath: tempDir,
      workflowLogRepo,
      issueId: 'uuid-42',
      issueNumber: 42,
    }, { maxRetries: 1 });

    expect(workflowLogRepo.insert).toHaveBeenCalledWith(
      'uuid-42',
      null,
      'task_started',
      expect.objectContaining({ taskId: 'T-001', attempt: 1 }),
    );

    expect(workflowLogRepo.insert).toHaveBeenCalledWith(
      'uuid-42',
      null,
      'task_completed',
      expect.objectContaining({ taskId: 'T-001', attempt: 1 }),
    );
  });

  it('should write task_failed to workflow_log on failure', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({
      text: '',
      success: false,
      error: '[SPAWN_FAILED] dependency error',
    }));

    const change = createMinimalChange();
    const workflowLogRepo = { insert: vi.fn() } as any;

    await runRalphLoop(change, {
      worktreePath: tempDir,
      projectPath: tempDir,
      workflowLogRepo,
      issueId: 'uuid-42',
      issueNumber: 42,
    }, { maxRetries: 1 });

    expect(workflowLogRepo.insert).toHaveBeenCalledWith(
      'uuid-42',
      null,
      'task_started',
      expect.objectContaining({ taskId: 'T-001' }),
    );

    expect(workflowLogRepo.insert).toHaveBeenCalledWith(
      'uuid-42',
      null,
      'task_failed',
      expect.objectContaining({ taskId: 'T-001', category: 'dependency' }),
    );
  });

  it('should emit SSE events for task progress via eventBus', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({
      text: 'done',
      success: true,
    }));

    const change = createMinimalChange();
    const eventBus = { emit: vi.fn() } as any;

    await runRalphLoop(change, {
      worktreePath: tempDir,
      projectPath: tempDir,
      eventBus,
      issueId: 'uuid-42',
      issueNumber: 42,
      projectId: 'proj-1',
      executionId: 'build-42',
    }, { maxRetries: 1 });

    expect(eventBus.emit).toHaveBeenCalledWith(
      'ralph_task_update',
      expect.objectContaining({ issueId: '42', taskId: 'T-001', status: 'started' }),
    );

    expect(eventBus.emit).toHaveBeenCalledWith(
      'ralph_task_update',
      expect.objectContaining({ issueId: '42', taskId: 'T-001', status: 'completed' }),
    );

    expect(eventBus.emit).toHaveBeenCalledWith(
      'ralph_loop_progress',
      expect.objectContaining({ issueId: '42', completed: 1, failed: 0, total: 1 }),
    );
  });

  it('should handle eventBus.emit failures gracefully', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({
      text: 'done',
      success: true,
    }));

    const change = createMinimalChange();
    const throwingEventBus = {
      emit: vi.fn().mockImplementation(() => {
        throw new Error('SSE broken');
      }),
    } as any;

    const result = await runRalphLoop(change, {
      worktreePath: tempDir,
      projectPath: tempDir,
      eventBus: throwingEventBus,
      issueNumber: 42,
    }, { maxRetries: 1 });

    expect(result.success).toBe(true);
    expect(result.completed).toBe(1);
    expect(throwingEventBus.emit).toHaveBeenCalled();
  });

  it('should write task_retrying to workflow_log on retry', async () => {
    let callCount = 0;
    setAcpSessionRunner(vi.fn().mockImplementation(() => {
      callCount++;
      if (callCount === 1) {
        return Promise.resolve({ text: '', success: false, error: 'AC not met' });
      }
      return Promise.resolve({ text: 'done', success: true });
    }));

    const change = createMinimalChange();
    const workflowLogRepo = { insert: vi.fn() } as any;

    await runRalphLoop(change, {
      worktreePath: tempDir,
      projectPath: tempDir,
      workflowLogRepo,
      issueId: 'uuid-42',
      issueNumber: 42,
    }, { maxRetries: 3 });

    expect(workflowLogRepo.insert).toHaveBeenCalledWith(
      'uuid-42',
      null,
      'task_retrying',
      expect.objectContaining({ taskId: 'T-001', category: 'ac_not_met' }),
    );
  });

  it('should write workflow_log entries even without eventBus', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({
      text: 'done',
      success: true,
    }));

    const change = createMinimalChange();
    const workflowLogRepo = { insert: vi.fn() } as any;

    await runRalphLoop(change, {
      worktreePath: tempDir,
      projectPath: tempDir,
      workflowLogRepo,
      issueId: 'uuid-42',
      issueNumber: 42,
    }, { maxRetries: 1 });

    expect(workflowLogRepo.insert).toHaveBeenCalled();
    const eventTypes = (workflowLogRepo.insert as ReturnType<typeof vi.fn>).mock.calls.map(
      (call: any[]) => call[2]
    );
    expect(eventTypes).toContain('task_started');
    expect(eventTypes).toContain('task_completed');
  });
});
