import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { runRalphLoop, sortTasksByOrder, getOrderValue, readTasks, findNextPendingTask, RalphExecutor, categorizeFailure, FAILURE_CATEGORY_CONFIGS, setAcpSessionRunner, resetAcpSessionRunner, validateTaskDependencies } from '../src/openspec/ralph-executor';
import { truncateAgentText } from '../src/agent-runtime/agent-session';
import type { OpenSpecChange } from '../src/openspec/detector';

describe('ralph-executor utilities', () => {
  describe('getOrderValue', () => {
    it('should return 999999 for undefined', () => {
      expect(getOrderValue(undefined)).toBe(999999);
    });

    it('should return the number itself for numeric order', () => {
      expect(getOrderValue(1)).toBe(1);
      expect(getOrderValue(5)).toBe(5);
      expect(getOrderValue(10)).toBe(10);
    });

  });

  describe('sortTasksByOrder', () => {
    it('should sort tasks by order value', () => {
      const tasks = [
        { id: 'T-003', order: 3, title: 'Third', description: 'desc' },
        { id: 'T-001', order: 1, title: 'First', description: 'desc' },
        { id: 'T-002', order: 2, title: 'Second', description: 'desc' },
      ];

      const sorted = sortTasksByOrder(tasks);
      expect(sorted.map(t => t.id)).toEqual(['T-001', 'T-002', 'T-003']);
    });

    it('should place undefined order at end', () => {
      const tasks = [
        { id: 'T-002', title: 'Second', description: 'desc' },
        { id: 'T-001', order: 1, title: 'First', description: 'desc' },
      ];

      const sorted = sortTasksByOrder(tasks);
      expect(sorted[sorted.length - 1].id).toBe('T-002');
    });
  });

  describe('readTasks', () => {
    let tempDir: string;

    beforeEach(() => {
      tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
    });

    afterEach(() => {
      fs.rmSync(tempDir, { recursive: true, force: true });
    });

    it('should return null when file does not exist', () => {
      const result = readTasks(path.join(tempDir, 'tasks.json'));
      expect(result).toBeNull();
    });

    it('should return null for invalid JSON without tasks array', () => {
      const tasksPath = path.join(tempDir, 'tasks.json');
      fs.writeFileSync(tasksPath, JSON.stringify({ version: 1 }));

      const result = readTasks(tasksPath);
      expect(result).toBeNull();
    });

    it('should return tasks array for valid tasks.json', () => {
      const tasksPath = path.join(tempDir, 'tasks.json');
      const tasksFile = {
        version: 1,
        tasks: [
          { id: 'T-001', title: 'Task 1', description: 'desc', passes: false, attempts: 0 },
          { id: 'T-002', title: 'Task 2', description: 'desc', passes: false, attempts: 0 },
        ],
      };
      fs.writeFileSync(tasksPath, JSON.stringify(tasksFile));

      const result = readTasks(tasksPath);
      expect(result).not.toBeNull();
      expect(result?.length).toBe(2);
      expect(result?.[0].id).toBe('T-001');
    });
  });

  describe('findNextPendingTask', () => {
    it('should return first task with passes===false by order', () => {
      const tasks = [
        { id: 'T-001', order: 1, title: 'First', description: 'desc', passes: true, attempts: 1 },
        { id: 'T-002', order: 2, title: 'Second', description: 'desc', passes: false, attempts: 0 },
        { id: 'T-003', order: 3, title: 'Third', description: 'desc', passes: false, attempts: 0 },
      ];
      const next = findNextPendingTask(tasks);
      expect(next?.id).toBe('T-002');
    });

    it('should return null when all tasks pass', () => {
      const tasks = [
        { id: 'T-001', order: 1, title: 'First', description: 'desc', passes: true, attempts: 1 },
        { id: 'T-002', order: 2, title: 'Second', description: 'desc', passes: true, attempts: 1 },
      ];
      const next = findNextPendingTask(tasks);
      expect(next).toBeNull();
    });

    it('should return null for empty array', () => {
      expect(findNextPendingTask([])).toBeNull();
    });
  });
});

describe('runRalphLoop', () => {
  let tempDir: string;
  let change: OpenSpecChange;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-ralph-test-'));
    setAcpSessionRunner(vi.fn().mockResolvedValue({
      text: 'done',
      success: true,
    }));
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
        { id: 'T-002', order: 2, title: 'Second Task', description: 'Do second thing', passes: false, attempts: 0 },
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

  it('should return error result when tasks.json does not exist', async () => {
    const fakeChange: OpenSpecChange = {
      changePath: tempDir,
      tasksPath: path.join(tempDir, 'nonexistent', 'tasks.json'),
      sessionMemoriesPath: path.join(tempDir, 'session-memories'),
      proposalPath: path.join(tempDir, 'proposal.md'),
      designPath: path.join(tempDir, 'design.md'),
      specsPath: path.join(tempDir, 'specs'),
    };

    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
    };

    const result = await runRalphLoop(fakeChange, context);

    expect(result.success).toBe(false);
    expect(result.total).toBe(0);
    expect(result.completed).toBe(0);
  });

  it('should write passes=true to tasks.json after execution', async () => {
    change = createMinimalChange();

    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
      onTaskStart: vi.fn(),
      onTaskComplete: vi.fn(),
    };

    await runRalphLoop(change, context, { maxRetries: 0 });

    const updated = JSON.parse(fs.readFileSync(change.tasksPath, 'utf-8'));
    expect(updated.tasks.length).toBe(2);
    expect(updated.tasks[0].id).toBe('T-001');
  });

  it('should return success immediately when all tasks have passes=true', async () => {
    change = createMinimalChange();

    const tasksFile = {
      version: 1,
      tasks: [
        { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: true, attempts: 0 },
        { id: 'T-002', order: 2, title: 'Second Task', description: 'desc', passes: true, attempts: 0 },
      ],
    };
    fs.writeFileSync(change.tasksPath, JSON.stringify(tasksFile));

    const onTaskStart = vi.fn();
    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
      onTaskStart,
    };

    const result = await runRalphLoop(change, context, { maxRetries: 0 });

    expect(onTaskStart).not.toHaveBeenCalled();
    expect(result.completed).toBe(2);
    expect(result.success).toBe(true);
    expect(result.skipped).toBe(2);

    const updated = JSON.parse(fs.readFileSync(change.tasksPath, 'utf-8'));
    expect(updated.tasks.every((t: any) => t.passes === true)).toBe(true);
  });

  describe('full checkpoint recovery', () => {
    it('short-circuits when skipTaskIds covers all tasks, returns completed=total', async () => {
      change = createMinimalChange();

      const tasksFile = {
        version: 1,
        tasks: [
          { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: false, attempts: 1 },
          { id: 'T-002', order: 2, title: 'Second Task', description: 'desc', passes: false, attempts: 1 },
        ],
      };
      fs.writeFileSync(change.tasksPath, JSON.stringify(tasksFile));

      const onTaskStart = vi.fn();
      const onLoopComplete = vi.fn();
      const context = {
        worktreePath: tempDir,
        projectPath: tempDir,
        onTaskStart,
        onLoopComplete,
      };

      const result = await runRalphLoop(change, context, {
        skipTaskIds: ['T-001', 'T-002'],
        maxRetries: 0,
      });

      expect(result.completed).toBe(2);
      expect(result.failed).toBe(0);
      expect(result.skipped).toBe(0);
      expect(result.total).toBe(2);
      expect(result.success).toBe(true);
      expect(result.taskResults).toHaveLength(0);
      expect(onTaskStart).not.toHaveBeenCalled();
      expect(onLoopComplete).toHaveBeenCalledTimes(1);
      expect(onLoopComplete.mock.calls[0][0].success).toBe(true);

      const updated = JSON.parse(fs.readFileSync(change.tasksPath, 'utf-8'));
      expect(updated.tasks.every((t: any) => t.passes === true)).toBe(true);
    });

    it('does NOT reset all-pass tasks when skipTaskIds is non-empty', async () => {
      change = createMinimalChange();

      const tasksFile = {
        version: 1,
        tasks: [
          { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: true, attempts: 1 },
          { id: 'T-002', order: 2, title: 'Second Task', description: 'desc', passes: true, attempts: 1 },
        ],
      };
      fs.writeFileSync(change.tasksPath, JSON.stringify(tasksFile));

      const onTaskStart = vi.fn();
      const context = {
        worktreePath: tempDir,
        projectPath: tempDir,
        onTaskStart,
      };

      const result = await runRalphLoop(change, context, {
        skipTaskIds: ['T-001', 'T-002'],
        maxRetries: 0,
      });

      expect(result.completed).toBe(2);
      expect(result.success).toBe(true);
      expect(onTaskStart).not.toHaveBeenCalled();

      const updated = JSON.parse(fs.readFileSync(change.tasksPath, 'utf-8'));
      expect(updated.tasks.every((t: any) => t.passes === true)).toBe(true);
    });

    it('returns success immediately when all tasks passed and skipTaskIds is empty', async () => {
      change = createMinimalChange();

      const tasksFile = {
        version: 1,
        tasks: [
          { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: true, attempts: 0 },
          { id: 'T-002', order: 2, title: 'Second Task', description: 'desc', passes: true, attempts: 0 },
        ],
      };
      fs.writeFileSync(change.tasksPath, JSON.stringify(tasksFile));

      const onTaskStart = vi.fn();
      const context = {
        worktreePath: tempDir,
        projectPath: tempDir,
        onTaskStart,
      };

      const result = await runRalphLoop(change, context, { maxRetries: 0 });

      expect(onTaskStart).not.toHaveBeenCalled();
      expect(result.completed).toBe(2);
      expect(result.success).toBe(true);
      expect(result.skipped).toBe(2);
    });

    it('partial skipTaskIds still enters main loop for remaining tasks', async () => {
      setAcpSessionRunner(vi.fn().mockResolvedValue({
        text: 'done',
        success: true,
      }));

      change = createMinimalChange();

      const tasksFile = {
        version: 1,
        tasks: [
          { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: false, attempts: 1 },
          { id: 'T-002', order: 2, title: 'Second Task', description: 'desc', passes: false, attempts: 0 },
        ],
      };
      fs.writeFileSync(change.tasksPath, JSON.stringify(tasksFile));

      const onTaskStart = vi.fn();
      const context = {
        worktreePath: tempDir,
        projectPath: tempDir,
        onTaskStart,
      };

      const result = await runRalphLoop(change, context, {
        skipTaskIds: ['T-001'],
        maxRetries: 0,
      });

      expect(result.completed).toBe(1);
      expect(result.total).toBe(2);
      expect(result.success).toBe(true);
      expect(onTaskStart).toHaveBeenCalledTimes(1);
    });
  });

  it('should not reset passes when tasks have mixed passes values', async () => {
    change = createMinimalChange();

    const tasksFile = {
      version: 1,
      tasks: [
        { id: 'T-001', order: 1, title: 'First Task', description: 'desc', passes: true, attempts: 1 },
        { id: 'T-002', order: 2, title: 'Second Task', description: 'desc', passes: false, attempts: 0 },
      ],
    };
    fs.writeFileSync(change.tasksPath, JSON.stringify(tasksFile));

    const onTaskStart = vi.fn();
    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
      onTaskStart,
    };

    const result = await runRalphLoop(change, context, { maxRetries: 0 });

    expect(onTaskStart).toHaveBeenCalledTimes(1);
    expect(result.completed).toBe(1);
    expect(result.total).toBe(2);
  });

  it('should pass correct context to onLoopComplete', async () => {
    change = createMinimalChange();

    const onLoopComplete = vi.fn();
    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
      onLoopComplete,
    };

    await runRalphLoop(change, context, { maxRetries: 0 });

    expect(onLoopComplete).toHaveBeenCalledTimes(1);
    const result = onLoopComplete.mock.calls[0][0];
    expect(result.total).toBe(2);
  });
});

describe('RalphExecutor class', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-ralph-test-'));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  it('should be constructable with context', () => {
    const executor = new RalphExecutor({
      worktreePath: tempDir,
      projectPath: tempDir,
    });
    expect(executor).toBeDefined();
  });
});

describe('Failure categorization', () => {
  describe('timeout category', () => {
    it('should categorize timeout errors', () => {
      expect(categorizeFailure('Timed out after 1800000ms')).toBe('timeout');
      expect(categorizeFailure('Request timeout')).toBe('timeout');
      expect(categorizeFailure('TIMEOUT ERROR')).toBe('timeout');
    });

    it('should match "timed out" case-insensitively', () => {
      expect(categorizeFailure('Operation timed out')).toBe('timeout');
      expect(categorizeFailure('TIMED OUT waiting for response')).toBe('timeout');
    });
  });

  describe('dependency category', () => {
    it('should categorize module not found errors', () => {
      expect(categorizeFailure('Cannot find module express')).toBe('dependency');
      expect(categorizeFailure('Module not found: foo')).toBe('dependency');
      expect(categorizeFailure('ERR_MODULE_NOT_FOUND')).toBe('dependency');
      expect(categorizeFailure('No such module: xyz')).toBe('dependency');
    });

    it('should categorize package resolution errors', () => {
      expect(categorizeFailure('Cannot find package @types/node')).toBe('dependency');
      expect(categorizeFailure('Package not found: lodash')).toBe('dependency');
      expect(categorizeFailure('Failed to resolve dependency')).toBe('dependency');
      expect(categorizeFailure('Could not be resolved from /src')).toBe('dependency');
    });

    it('should categorize import errors', () => {
      expect(categorizeFailure('Import error: missing export')).toBe('dependency');
      expect(categorizeFailure('Unresolved import ./utils')).toBe('dependency');
    });

    it('should categorize npm dependency errors', () => {
      expect(categorizeFailure('Unmet dependency: react@18')).toBe('dependency');
      expect(categorizeFailure('Peer dependency missing: webpack')).toBe('dependency');
      expect(categorizeFailure('dependency not installed')).toBe('dependency');
    });
  });

  describe('environment category', () => {
    it('should categorize npm/install errors', () => {
      expect(categorizeFailure('npm install failed')).toBe('environment');
      expect(categorizeFailure('node_modules missing')).toBe('environment');
      expect(categorizeFailure('Install failed: network')).toBe('environment');
    });

    it('should categorize file system errors', () => {
      expect(categorizeFailure('ENOENT: no such file')).toBe('environment');
      expect(categorizeFailure('Permission denied: /root')).toBe('environment');
      expect(categorizeFailure('No such file or directory')).toBe('environment');
    });

    it('should categorize command errors', () => {
      expect(categorizeFailure('Command not found: npm')).toBe('environment');
      expect(categorizeFailure('spawn ENOENT')).toBe('environment');
      expect(categorizeFailure('Spawn error: process failed')).toBe('environment');
      expect(categorizeFailure('Spawn failed: git')).toBe('environment');
    });

    it('should categorize network errors', () => {
      expect(categorizeFailure('ECONNREFUSED: connection refused')).toBe('environment');
      expect(categorizeFailure('ECONNRESET: connection reset')).toBe('environment');
      expect(categorizeFailure('Network error: fetch failed')).toBe('environment');
      expect(categorizeFailure('Network request failed: connection refused')).toBe('environment');
    });

    it('should categorize memory errors', () => {
      expect(categorizeFailure('FATAL ERROR: heap out of memory')).toBe('environment');
      expect(categorizeFailure('JavaScript out of memory')).toBe('environment');
    });

    it('should categorize system errors', () => {
      expect(categorizeFailure('EACCES: permission denied')).toBe('environment');
      expect(categorizeFailure('ENOSPC: no space left on device')).toBe('environment');
      expect(categorizeFailure('Disk full: cannot write')).toBe('environment');
      expect(categorizeFailure('Segmentation fault (core dumped)')).toBe('environment');
    });

    it('should categorize environment variable errors', () => {
      expect(categorizeFailure('Missing environment variable API_KEY')).toBe('environment');
    });
  });

  describe('ac_not_met (default) category', () => {
    it('should categorize test assertion failures', () => {
      expect(categorizeFailure('Test assertion failed')).toBe('ac_not_met');
      expect(categorizeFailure('expect(received).toBe(expected)')).toBe('ac_not_met');
      expect(categorizeFailure('FAIL src/app.test.ts')).toBe('ac_not_met');
    });

    it('should categorize compilation/typecheck errors', () => {
      expect(categorizeFailure('Type error: string is not assignable')).toBe('ac_not_met');
      expect(categorizeFailure('SyntaxError: Unexpected token')).toBe('ac_not_met');
      expect(categorizeFailure('error TS2322: type mismatch')).toBe('ac_not_met');
    });

    it('should categorize lint errors', () => {
      expect(categorizeFailure('Lint error: unused variable')).toBe('ac_not_met');
      expect(categorizeFailure('eslint: no-unused-vars')).toBe('ac_not_met');
    });

    it('should categorize generic implementation errors', () => {
      expect(categorizeFailure('AC not satisfied')).toBe('ac_not_met');
      expect(categorizeFailure('Missing validation')).toBe('ac_not_met');
      expect(categorizeFailure('Unknown error')).toBe('ac_not_met');
    });
  });

  describe('hang_unrecoverable category', () => {
    it('should categorize [HANG_UNRECOVERABLE] errors', () => {
      expect(categorizeFailure('[HANG_UNRECOVERABLE] max recovery attempts exceeded')).toBe('hang_unrecoverable');
      expect(categorizeFailure('[HANG_UNRECOVERABLE] cancel timed out')).toBe('hang_unrecoverable');
    });

    it('should match [HANG_UNRECOVERABLE] anywhere in error string', () => {
      expect(categorizeFailure('Error: [HANG_UNRECOVERABLE] something went wrong')).toBe('hang_unrecoverable');
      expect(categorizeFailure('prefix [HANG_UNRECOVERABLE] suffix')).toBe('hang_unrecoverable');
    });
  });

  describe('timeout_with_wip category', () => {
    it('should categorize timeout errors with wipCommitted=true as timeout_with_wip', () => {
      expect(categorizeFailure('Timed out after 1800s', { wipCommitted: true })).toBe('timeout_with_wip');
      expect(categorizeFailure('Request timeout', { wipCommitted: true })).toBe('timeout_with_wip');
    });

    it('should categorize timeout errors with wipCommitted=false as timeout', () => {
      expect(categorizeFailure('Timed out after 1800s', { wipCommitted: false })).toBe('timeout');
    });

    it('should default to timeout without wipCommitted', () => {
      expect(categorizeFailure('Timed out after 1800s')).toBe('timeout');
    });
  });

  describe('precedence rules', () => {
    it('should classify hang_unrecoverable before timeout', () => {
      expect(categorizeFailure('[HANG_UNRECOVERABLE] Timed out after 1800s')).toBe('hang_unrecoverable');
    });

    it('should classify timeout before dependency', () => {
      expect(categorizeFailure('Timeout: cannot find module')).toBe('timeout');
    });

    it('should classify timeout before environment', () => {
      expect(categorizeFailure('Timeout waiting for npm install')).toBe('timeout');
    });

    it('should classify dependency before environment', () => {
      expect(categorizeFailure('Cannot find module in node_modules')).toBe('dependency');
    });
  });

  it('should have correct config for each category', () => {
    expect(FAILURE_CATEGORY_CONFIGS.ac_not_met.maxAttempts).toBe(3);
    expect(FAILURE_CATEGORY_CONFIGS.ac_not_met.retryable).toBe(true);
    expect(FAILURE_CATEGORY_CONFIGS.environment.maxAttempts).toBe(2);
    expect(FAILURE_CATEGORY_CONFIGS.environment.retryable).toBe(true);
    expect(FAILURE_CATEGORY_CONFIGS.dependency.maxAttempts).toBe(1);
    expect(FAILURE_CATEGORY_CONFIGS.dependency.retryable).toBe(false);
    expect(FAILURE_CATEGORY_CONFIGS.timeout.maxAttempts).toBe(3);
    expect(FAILURE_CATEGORY_CONFIGS.timeout.retryable).toBe(true);
    expect(FAILURE_CATEGORY_CONFIGS.timeout_with_wip.maxAttempts).toBe(2);
    expect(FAILURE_CATEGORY_CONFIGS.timeout_with_wip.retryable).toBe(true);
    expect(FAILURE_CATEGORY_CONFIGS.hang_unrecoverable.maxAttempts).toBe(1);
    expect(FAILURE_CATEGORY_CONFIGS.hang_unrecoverable.retryable).toBe(false);
  });
});

describe('resource cleanup', () => {
  it('should call cleanup via Promise.allSettled pattern', async () => {
    const { ReadableStream, WritableStream } = await import('stream/web');
    const readableCancel = vi.fn().mockResolvedValue(undefined);
    const writableAbort = vi.fn().mockResolvedValue(undefined);
    const mockStream = {
      readable: { cancel: readableCancel } as unknown as ReadableStream,
      writable: { abort: writableAbort } as unknown as WritableStream,
    };
    const results = await Promise.allSettled([
      mockStream.readable.cancel().catch(() => {}),
      mockStream.writable.abort().catch(() => {}),
    ]);
    expect(results).toHaveLength(2);
    expect(results[0].status).toBe('fulfilled');
    expect(results[1].status).toBe('fulfilled');
    expect(readableCancel).toHaveBeenCalledTimes(1);
    expect(writableAbort).toHaveBeenCalledTimes(1);
  });

  it('should continue cleanup even if one operation fails', async () => {
    const readableCancel = vi.fn().mockRejectedValue(new Error('read error'));
    const writableAbort = vi.fn().mockResolvedValue(undefined);
    const mockStream = {
      readable: { cancel: readableCancel },
      writable: { abort: writableAbort },
    };
    const results = await Promise.allSettled([
      mockStream.readable.cancel().catch(() => {}),
      mockStream.writable.abort().catch(() => {}),
    ]);
    expect(readableCancel).toHaveBeenCalledTimes(1);
    expect(writableAbort).toHaveBeenCalledTimes(1);
  });

  it('should use atomic flag to prevent duplicate cleanup (doCleanup idempotency)', () => {
    let cleanupDone = false;
    let killCount = 0;
    const doCleanup = () => {
      if (cleanupDone) return;
      cleanupDone = true;
      killCount++;
    };

    doCleanup();
    doCleanup();
    doCleanup();

    expect(killCount).toBe(1);
    expect(cleanupDone).toBe(true);
  });

  it('should clear timeout in doCleanup to prevent race condition', () => {
    let cleanupDone = false;
    let timeoutId: NodeJS.Timeout | null = setTimeout(() => {}, 100000);
    let clearTimeoutCalled = false;
    const originalClearTimeout = global.clearTimeout;
    global.clearTimeout = ((id: any) => {
      clearTimeoutCalled = true;
      originalClearTimeout(id);
    }) as typeof clearTimeout;

    const doCleanup = () => {
      if (cleanupDone) return;
      cleanupDone = true;
      if (timeoutId) {
        clearTimeout(timeoutId);
        timeoutId = null;
      }
    };

    doCleanup();
    doCleanup();

    expect(clearTimeoutCalled).toBe(true);
    expect(timeoutId).toBeNull();
    expect(cleanupDone).toBe(true);

    global.clearTimeout = originalClearTimeout;
  });

  it('should handle concurrent cleanup calls from multiple paths safely', () => {
    let cleanupDone = false;
    let killCount = 0;
    const doCleanup = () => {
      if (cleanupDone) return;
      cleanupDone = true;
      killCount++;
    };

    const results = Array.from({ length: 100 }, () => {
      doCleanup();
      return cleanupDone;
    });

    expect(killCount).toBe(1);
    expect(results.every(r => r === true)).toBe(true);
  });
});

describe('agentText truncation', () => {
  const MAX = 2 * 1024 * 1024;

  it('should not truncate text under 2MB', () => {
    const text = 'a'.repeat(MAX - 1);
    expect(truncateAgentText(text)).toBe(text);
  });

  it('should not truncate text exactly at 2MB', () => {
    const text = 'a'.repeat(MAX);
    expect(truncateAgentText(text)).toBe(text);
  });

  it('should truncate text over 2MB preserving head and tail', () => {
    const head = 'HEAD'.repeat(100);
    const middle = 'M'.repeat(MAX + 100000);
    const tail = 'TAIL'.repeat(100);
    const text = head + middle + tail;

    const result = truncateAgentText(text);

    expect(result.length).toBeLessThan(text.length);
    expect(result.startsWith(head)).toBe(true);
    expect(result.endsWith(tail)).toBe(true);
    expect(result).toContain('...[truncated ');
    expect(result).toContain(' characters]...');
  });

  it('should report correct truncated character count', () => {
    const text = 'a'.repeat(MAX + 5000);
    const result = truncateAgentText(text);
    const match = result.match(/\.\.\.\[truncated (\d+) characters\]\.\.\./);
    expect(match).not.toBeNull();
    expect(parseInt(match![1], 10)).toBe(5000);
  });
});

describe('v4 bug fix: failed counter and auto-skip', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-v4-test-'));
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

  it('auto-skipped task increments failed and skipped counters', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({
      success: false,
      error: 'Timed out after 1800000ms',
    }));

    const change = createChangeWithTasks(1);
    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
    };

    const result = await runRalphLoop(change, context, { maxRetries: 0 });

    expect(result.failed).toBe(1);
    expect(result.skipped).toBe(1);
    expect(result.success).toBe(false);
    expect(result.taskResults).toHaveLength(1);
    expect(result.taskResults[0].status).toBe('skipped');
  });

  it('retry success replaces failed taskResult with completed', async () => {
    let callCount = 0;
    setAcpSessionRunner(vi.fn().mockImplementation(() => {
      callCount++;
      if (callCount === 1) {
        return Promise.resolve({ success: false, error: 'Timed out' });
      }
      return Promise.resolve({ success: true, text: 'done' });
    }));

    const change = createChangeWithTasks(1);
    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
      onAskUser: vi.fn().mockResolvedValue('retry'),
    };

    const result = await runRalphLoop(change, context, { maxRetries: 0 });

    expect(result.failed).toBe(0);
    expect(result.success).toBe(true);
    expect(result.taskResults).toHaveLength(1);
    expect(result.taskResults[0].status).toBe('completed');
  });

  it('genuinely failed task (abort) increments failed counter', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({
      success: false,
      error: 'Timed out after 1800000ms',
    }));

    const change = createChangeWithTasks(1);
    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
      onAskUser: vi.fn().mockResolvedValue('abort'),
    };

    const result = await runRalphLoop(change, context, { maxRetries: 0 });

    expect(result.failed).toBe(1);
    expect(result.success).toBe(false);
    expect(result.taskResults[0].status).toBe('failed');
  });
});

describe('session failure propagation', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-session-fail-'));
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
      description: `desc`,
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

  it('session_failed result records task attempt failure and passes failureReason to caller', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({
      success: false,
      error: 'Session liveness probe timed out',
      failureKind: 'session_failed',
      failureReason: 'probe_timeout',
    }));

    const change = createChangeWithTasks(1);
    const onAskUser = vi.fn().mockResolvedValue('abort');
    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
      issueId: 'issue-42',
      onAskUser,
    };

    const result = await runRalphLoop(change, context, { maxRetries: 0 });

    expect(result.failed).toBe(1);
    expect(result.success).toBe(false);
    expect(result.taskResults[0].status).toBe('failed');
    expect(result.taskResults[0].error).toBe('Session liveness probe timed out');
  });

  it('session_failed does not set passes=true on the task', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({
      success: false,
      error: 'Session liveness probe timed out',
      failureKind: 'session_failed',
      failureReason: 'probe_timeout',
    }));

    const change = createChangeWithTasks(1);
    const onAskUser = vi.fn().mockResolvedValue('abort');
    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
      issueId: 'issue-42',
      onAskUser,
    };

    await runRalphLoop(change, context, { maxRetries: 0 });

    const updated = JSON.parse(fs.readFileSync(change.tasksPath, 'utf-8'));
    expect(updated.tasks[0].passes).toBe(false);
  });

  it('session_failed task retried when retryable with maxRetries>0', async () => {
    let callCount = 0;
    setAcpSessionRunner(vi.fn().mockImplementation(() => {
      callCount++;
      if (callCount === 1) {
        return Promise.resolve({
          success: false,
          error: 'Session liveness probe timed out',
          failureKind: 'session_failed',
          failureReason: 'probe_timeout',
        });
      }
      return Promise.resolve({ success: true, text: 'done' });
    }));

    const change = createChangeWithTasks(1);
    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
      issueId: 'issue-42',
    };

    const result = await runRalphLoop(change, context, { maxRetries: 2 });

    expect(result.completed).toBe(1);
    expect(result.failed).toBe(0);
    expect(result.success).toBe(true);
    expect(callCount).toBe(2);
  });

  it('distinguishes session_failed from timeout in failureKind metadata', async () => {
    const results: any[] = [];
    setAcpSessionRunner(vi.fn().mockImplementation((opts: any) => {
      results.push(opts);
      return Promise.resolve({
        success: false,
        error: 'Timed out',
        failureKind: 'timeout',
      });
    }));

    const change = createChangeWithTasks(1);
    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
      issueId: 'issue-42',
    };

    await runRalphLoop(change, context, { maxRetries: 0 });

    expect(results[0].observers?.[0]?.onLivenessUpdate).toBeDefined();
  });
});

describe('v4 bug fix: stage timeout calculation', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-v4-timeout-'));
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
      description: `desc`,
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

  it('uses taskTimeout from config regardless of stage timeout', async () => {
    const capturedTimeouts: number[] = [];
    setAcpSessionRunner(vi.fn().mockImplementation((opts: any) => {
      capturedTimeouts.push(opts.timeout);
      return Promise.resolve({ success: true, text: 'done' });
    }));

    const change = createChangeWithTasks(2);
    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
      stageTimeoutMs: 1800 * 1000,
    };

    await runRalphLoop(change, context, { maxRetries: 0 });

    expect(capturedTimeouts).toHaveLength(2);
    expect(capturedTimeouts[0]).toBe(1800_000);
    expect(capturedTimeouts[1]).toBe(1800_000);
  });

  it('uses taskTimeout from config even when stage timeout is very small', async () => {
    const capturedTimeouts: number[] = [];
    setAcpSessionRunner(vi.fn().mockImplementation((opts: any) => {
      capturedTimeouts.push(opts.timeout);
      return Promise.resolve({ success: true, text: 'done' });
    }));

    const change = createChangeWithTasks(10);
    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
      stageTimeoutMs: 60 * 1000,
    };

    await runRalphLoop(change, context, { maxRetries: 0 });

    expect(capturedTimeouts).toHaveLength(10);
    for (const t of capturedTimeouts) {
      expect(t).toBe(1800_000);
    }
  });

  it('uses config taskTimeout when no stage timeout configured', async () => {
    const capturedTimeouts: number[] = [];
    setAcpSessionRunner(vi.fn().mockImplementation((opts: any) => {
      capturedTimeouts.push(opts.timeout);
      return Promise.resolve({ success: true, text: 'done' });
    }));

    const change = createChangeWithTasks(2);
    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
    };

    await runRalphLoop(change, context, { maxRetries: 0 });

    expect(capturedTimeouts).toHaveLength(2);
    for (const t of capturedTimeouts) {
      expect(t).toBe(1800 * 1000);
    }
  });
});

describe('validateTaskDependencies', () => {
  it('valid dependency graph passes validation', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: [] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
      { id: 'T-003', order: 3, title: 'C', description: 'd', passes: false, attempts: 0, dependsOn: ['T-002'] },
    ];
    const result = validateTaskDependencies(tasks);
    expect(result.valid).toBe(true);
    expect(result.errors).toHaveLength(0);
  });

  it('unknown task ID in dependsOn fails validation', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-999'] },
    ];
    const result = validateTaskDependencies(tasks);
    expect(result.valid).toBe(false);
    expect(result.errors.some(e => e.includes('T-999') && e.includes('does not exist'))).toBe(true);
  });

  it('circular dependency detected', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-002'] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
    ];
    const result = validateTaskDependencies(tasks);
    expect(result.valid).toBe(false);
    expect(result.errors.some(e => e.toLowerCase().includes('circular'))).toBe(true);
  });

  it('forward dependency detected (dependsOn references higher-order task)', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-002'] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: [] },
    ];
    const result = validateTaskDependencies(tasks);
    expect(result.valid).toBe(false);
    expect(result.errors.some(e => e.includes('lower or equal order'))).toBe(true);
  });

  it('empty dependsOn is valid', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: [] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0 },
    ];
    const result = validateTaskDependencies(tasks);
    expect(result.valid).toBe(true);
    expect(result.errors).toHaveLength(0);
  });

  it('multi-level dependency chain passes', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: [] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
      { id: 'T-003', order: 3, title: 'C', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001', 'T-002'] },
      { id: 'T-004', order: 4, title: 'D', description: 'd', passes: false, attempts: 0, dependsOn: ['T-003'] },
    ];
    const result = validateTaskDependencies(tasks);
    expect(result.valid).toBe(true);
  });

  it('multiple errors are all reported', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-999'] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
      { id: 'T-003', order: 3, title: 'C', description: 'd', passes: false, attempts: 0, dependsOn: ['T-004'] },
    ];
    const result = validateTaskDependencies(tasks);
    expect(result.valid).toBe(false);
    expect(result.errors.length).toBeGreaterThanOrEqual(2);
  });
});

describe('findNextPendingTask with dependencies', () => {
  it('skips task whose dependsOn has not passed', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: [] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
    ];
    const next = findNextPendingTask(tasks);
    expect(next?.id).toBe('T-001');
  });

  it('picks task whose dependsOn has all passed', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: true, attempts: 1, dependsOn: [] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
      { id: 'T-003', order: 3, title: 'C', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001', 'T-002'] },
    ];
    const next = findNextPendingTask(tasks);
    expect(next?.id).toBe('T-002');
  });

  it('among multiple ready tasks, picks lowest order', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: true, attempts: 1, dependsOn: [] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: [] },
      { id: 'T-003', order: 3, title: 'C', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
    ];
    const next = findNextPendingTask(tasks);
    expect(next?.id).toBe('T-002');
  });

  it('returns null when all pending tasks are blocked (deadlock)', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-002'] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
    ];
    const next = findNextPendingTask(tasks);
    expect(next).toBeNull();
  });

  it('task with empty dependsOn is always ready', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0 },
    ];
    const next = findNextPendingTask(tasks);
    expect(next?.id).toBe('T-001');
  });

  it('skips task with partially met dependsOn', () => {
    const tasks = [
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: true, attempts: 1 },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0 },
      { id: 'T-003', order: 3, title: 'C', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001', 'T-002'] },
    ];
    const next = findNextPendingTask(tasks);
    expect(next?.id).toBe('T-002');
  });
});

describe('runRalphLoop dependency integration', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-dep-test-'));
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

  it('rejects invalid graph on load with failed result', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

    const change = createChangeWithTasks([
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-999'] },
    ]);
    const context = { worktreePath: tempDir, projectPath: tempDir };

    const result = await runRalphLoop(change, context);

    expect(result.success).toBe(false);
    expect(result.failed).toBe(1);
    expect(result.completed).toBe(0);
    expect(result.pauseReason).toContain('validation failed');
  });

  it('executes tasks respecting dependency order', async () => {
    const executionOrder: string[] = [];
    setAcpSessionRunner(vi.fn().mockImplementation((opts: any) => {
      executionOrder.push(opts.taskId);
      return Promise.resolve({ success: true, text: 'done' });
    }));

    const change = createChangeWithTasks([
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: [] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
      { id: 'T-003', order: 3, title: 'C', description: 'd', passes: false, attempts: 0, dependsOn: ['T-002'] },
    ]);
    const context = { worktreePath: tempDir, projectPath: tempDir };

    const result = await runRalphLoop(change, context, { maxRetries: 0 });

    expect(result.success).toBe(true);
    expect(executionOrder).toEqual(['T-001', 'T-002', 'T-003']);
  });

  it('circular dependency rejected at validation, never enters loop', async () => {
    const sessionRunner = vi.fn().mockResolvedValue({ success: true, text: 'done' });
    setAcpSessionRunner(sessionRunner);

    const change = createChangeWithTasks([
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-002'] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
    ]);
    const context = { worktreePath: tempDir, projectPath: tempDir };

    const result = await runRalphLoop(change, context);

    expect(result.success).toBe(false);
    expect(result.failed).toBe(2);
    expect(result.pauseReason).toContain('validation failed');
    expect(sessionRunner).not.toHaveBeenCalled();
  });

  it('handles valid tasks with no dependencies (backward compatible)', async () => {
    const executionOrder: string[] = [];
    setAcpSessionRunner(vi.fn().mockImplementation((opts: any) => {
      executionOrder.push(opts.taskId);
      return Promise.resolve({ success: true, text: 'done' });
    }));

    const change = createChangeWithTasks([
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0 },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0 },
    ]);
    const context = { worktreePath: tempDir, projectPath: tempDir };

    const result = await runRalphLoop(change, context, { maxRetries: 0 });

    expect(result.success).toBe(true);
    expect(executionOrder).toEqual(['T-001', 'T-002']);
  });

  it('skips already-passed task and picks next ready by dependency', async () => {
    const executionOrder: string[] = [];
    setAcpSessionRunner(vi.fn().mockImplementation((opts: any) => {
      executionOrder.push(opts.taskId);
      return Promise.resolve({ success: true, text: 'done' });
    }));

    const change = createChangeWithTasks([
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: true, attempts: 1, dependsOn: [] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
      { id: 'T-003', order: 3, title: 'C', description: 'd', passes: false, attempts: 0, dependsOn: ['T-002'] },
    ]);
    const context = { worktreePath: tempDir, projectPath: tempDir };

    const result = await runRalphLoop(change, context, { maxRetries: 0 });

    expect(result.success).toBe(true);
    expect(executionOrder).toEqual(['T-002', 'T-003']);
    expect(result.completed).toBe(2);
  });

  it('onLoopComplete is called on validation failure', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

    const change = createChangeWithTasks([
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-999'] },
    ]);
    const onLoopComplete = vi.fn();
    const context = { worktreePath: tempDir, projectPath: tempDir, onLoopComplete };

    await runRalphLoop(change, context);

    expect(onLoopComplete).toHaveBeenCalledTimes(1);
    const loopResult = onLoopComplete.mock.calls[0][0];
    expect(loopResult.success).toBe(false);
    expect(loopResult.pauseReason).toContain('validation failed');
  });

  it('circular dependency in tasks.json causes validation failure', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

    const change = createChangeWithTasks([
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-002'] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: ['T-001'] },
    ]);
    const context = { worktreePath: tempDir, projectPath: tempDir };

    const result = await runRalphLoop(change, context);

    expect(result.success).toBe(false);
    expect(result.pauseReason).toContain('validation failed');
  });

  it('forward dependency in tasks.json causes validation failure', async () => {
    setAcpSessionRunner(vi.fn().mockResolvedValue({ success: true, text: 'done' }));

    const change = createChangeWithTasks([
      { id: 'T-001', order: 1, title: 'A', description: 'd', passes: false, attempts: 0, dependsOn: ['T-002'] },
      { id: 'T-002', order: 2, title: 'B', description: 'd', passes: false, attempts: 0, dependsOn: [] },
    ]);
    const context = { worktreePath: tempDir, projectPath: tempDir };

    const result = await runRalphLoop(change, context);

    expect(result.success).toBe(false);
    expect(result.pauseReason).toContain('validation failed');
  });
});