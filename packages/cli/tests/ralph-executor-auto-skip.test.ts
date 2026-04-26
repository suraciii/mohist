import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { runRalphLoop, setAcpSessionRunner, resetAcpSessionRunner } from '../src/openspec/ralph-executor';
import type { OpenSpecChange } from '../src/openspec/detector';

describe('auto-skip failure handling', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-autoskip-test-'));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
    resetAcpSessionRunner();
  });

  function createChangeWithTasks(taskCount: number): OpenSpecChange {
    const changeDir = path.join(tempDir, 'openspec', 'changes', '42-test');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.mkdirSync(path.join(changeDir, 'session-memories'), { recursive: true });

    const tasks = Array.from({ length: taskCount }, (_, i) => ({
      id: `T-${String(i + 1).padStart(3, '0')}`,
      order: i + 1,
      title: `Task ${i + 1}`,
      description: `Do task ${i + 1}`,
      passes: false,
      attempts: 0,
    }));
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

  it('non-retryable failure (timeout) without onAskUser → passes=false, failed=1, skipped=1, success=false', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({
      success: false,
      error: 'Timed out after 300s',
    }));

    const change = createChangeWithTasks(1);
    const context = { worktreePath: tempDir, projectPath: tempDir };
    const result = await runRalphLoop(change, context, { maxRetries: 0 });

    expect(result.failed).toBe(1);
    expect(result.skipped).toBe(1);
    expect(result.success).toBe(false);
    expect(result.taskResults).toHaveLength(1);
    expect(result.taskResults[0].status).toBe('skipped');

    const tasksFile = JSON.parse(fs.readFileSync(change.tasksPath, 'utf-8'));
    expect(tasksFile.tasks[0].passes).toBe(false);
    expect(tasksFile.tasks[0].error).toContain('Auto-skipped');
  });

  it('max retries exceeded without onAskUser → passes=false, failed=1, skipped=1, success=false', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({
      success: false,
      error: 'Build failed: npm test exited with code 1',
    }));

    const change = createChangeWithTasks(1);
    const context = { worktreePath: tempDir, projectPath: tempDir };
    const result = await runRalphLoop(change, context, { maxRetries: 3 });

    expect(result.failed).toBe(1);
    expect(result.skipped).toBe(1);
    expect(result.success).toBe(false);
    expect(result.taskResults).toHaveLength(1);
    expect(result.taskResults[0].status).toBe('skipped');

    const tasksFile = JSON.parse(fs.readFileSync(change.tasksPath, 'utf-8'));
    expect(tasksFile.tasks[0].passes).toBe(false);
    expect(tasksFile.tasks[0].error).toContain('Auto-skipped');
  });

  it('all tasks pass → failed=0, skipped=0, success=true', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({
      success: true,
      text: 'All tests passed',
    }));

    const change = createChangeWithTasks(3);
    const context = { worktreePath: tempDir, projectPath: tempDir };
    const result = await runRalphLoop(change, context, { maxRetries: 0 });

    expect(result.failed).toBe(0);
    expect(result.skipped).toBe(0);
    expect(result.success).toBe(true);
    expect(result.taskResults).toHaveLength(3);
    expect(result.taskResults.every(r => r.status === 'completed')).toBe(true);
  });

  it('task fails but onAskUser is provided → onAskUser is called (no auto-skip)', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({
      success: false,
      error: 'Timed out after 300s',
    }));

    const onAskUser = vi.fn().mockResolvedValue('abort');

    const change = createChangeWithTasks(1);
    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
      onAskUser,
    };
    const result = await runRalphLoop(change, context, { maxRetries: 0 });

    expect(onAskUser).toHaveBeenCalledTimes(1);
    expect(onAskUser).toHaveBeenCalledWith(expect.any(String), 'T-001');
    expect(result.failed).toBe(1);
    expect(result.success).toBe(false);
    expect(result.paused).toBe(true);
    expect(result.taskResults[0].status).toBe('failed');
  });
});
