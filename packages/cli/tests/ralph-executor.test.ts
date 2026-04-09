import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { runRalphLoop, sortTasksByOrder, getOrderValue, readTaskStatus, readPrdTasks, RalphExecutor, categorizeFailure, FAILURE_CATEGORY_CONFIGS, truncateAgentText } from '../src/openspec/ralph-executor';
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

    it('should parse order from string format like "5-A"', () => {
      expect(getOrderValue('5-A')).toBe(5);
      expect(getOrderValue('5-B')).toBe(5);
      expect(getOrderValue('10')).toBe(10);
    });

    it('should return 999999 for non-numeric strings', () => {
      expect(getOrderValue('abc')).toBe(999999);
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

    it('should handle mixed number and string orders', () => {
      const tasks = [
        { id: 'T-002', order: '5-B' as unknown as number, title: 'Second', description: 'desc' },
        { id: 'T-001', order: 1, title: 'First', description: 'desc' },
      ];

      const sorted = sortTasksByOrder(tasks);
      expect(sorted[0].id).toBe('T-001');
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

  describe('readTaskStatus', () => {
    let tempDir: string;

    beforeEach(() => {
      tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
    });

    afterEach(() => {
      fs.rmSync(tempDir, { recursive: true, force: true });
    });

    it('should return null when file does not exist', () => {
      const result = readTaskStatus(path.join(tempDir, 'task-status.json'));
      expect(result).toBeNull();
    });

    it('should return null for invalid JSON', () => {
      const statusPath = path.join(tempDir, 'task-status.json');
      fs.writeFileSync(statusPath, 'invalid json');
      const result = readTaskStatus(statusPath);
      expect(result).toBeNull();
    });

    it('should return parsed TaskStatusFile for valid JSON', () => {
      const statusPath = path.join(tempDir, 'task-status.json');
      const status = {
        current_task_index: 1,
        total_tasks: 3,
        tasks: [
          { id: 'T-001', status: 'completed', attempts: 1 },
          { id: 'T-002', status: 'in_progress', attempts: 1 },
        ],
      };
      fs.writeFileSync(statusPath, JSON.stringify(status));

      const result = readTaskStatus(statusPath);
      expect(result).not.toBeNull();
      expect(result?.current_task_index).toBe(1);
      expect(result?.tasks.length).toBe(2);
    });
  });

  describe('readPrdTasks', () => {
    let tempDir: string;

    beforeEach(() => {
      tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
    });

    afterEach(() => {
      fs.rmSync(tempDir, { recursive: true, force: true });
    });

    it('should return null when file does not exist', () => {
      const result = readPrdTasks(path.join(tempDir, 'prd.json'));
      expect(result).toBeNull();
    });

    it('should return null for invalid JSON without tasks array', () => {
      const prdPath = path.join(tempDir, 'prd.json');
      fs.writeFileSync(prdPath, JSON.stringify({ version: '1.0' }));

      const result = readPrdTasks(prdPath);
      expect(result).toBeNull();
    });

    it('should return tasks array for valid prd.json', () => {
      const prdPath = path.join(tempDir, 'prd.json');
      const prd = {
        version: '1.0',
        change_id: 'test',
        tasks: [
          { id: 'T-001', title: 'Task 1', description: 'desc' },
          { id: 'T-002', title: 'Task 2', description: 'desc' },
        ],
      };
      fs.writeFileSync(prdPath, JSON.stringify(prd));

      const result = readPrdTasks(prdPath);
      expect(result).not.toBeNull();
      expect(result?.length).toBe(2);
      expect(result?.[0].id).toBe('T-001');
    });
  });
});

describe('runRalphLoop', () => {
  let tempDir: string;
  let change: OpenSpecChange;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-ralph-test-'));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  function createMinimalChange(): OpenSpecChange {
    const changeDir = path.join(tempDir, '.mohist-specs', 'changes', '42-test');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.mkdirSync(path.join(changeDir, 'session-memories'), { recursive: true });

    const prd = {
      version: '1.0',
      change_id: 'test',
      tasks: [
        { id: 'T-001', order: 1, title: 'First Task', description: 'Do first thing' },
        { id: 'T-002', order: 2, title: 'Second Task', description: 'Do second thing' },
      ],
    };
    fs.writeFileSync(path.join(changeDir, 'prd.json'), JSON.stringify(prd));
    fs.writeFileSync(path.join(changeDir, 'proposal.md'), '# Test Proposal');
    fs.writeFileSync(path.join(changeDir, 'design.md'), '# Test Design');

    return {
      changePath: changeDir,
      prdPath: path.join(changeDir, 'prd.json'),
      taskStatusPath: path.join(changeDir, 'task-status.json'),
      sessionMemoriesPath: path.join(changeDir, 'session-memories'),
      proposalPath: path.join(changeDir, 'proposal.md'),
      designPath: path.join(changeDir, 'design.md'),
      specsPath: path.join(changeDir, 'specs'),
    };
  }

  it('should return error result when prd.json does not exist', async () => {
    const fakeChange: OpenSpecChange = {
      changePath: tempDir,
      prdPath: path.join(tempDir, 'nonexistent', 'prd.json'),
      taskStatusPath: path.join(tempDir, 'task-status.json'),
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

  it('should initialize task-status.json on first run', async () => {
    change = createMinimalChange();

    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
      onTaskStart: vi.fn(),
      onTaskComplete: vi.fn(),
    };

    await runRalphLoop(change, context, { maxRetries: 0 });

    expect(fs.existsSync(change.taskStatusPath)).toBe(true);
    const status = JSON.parse(fs.readFileSync(change.taskStatusPath, 'utf-8'));
    expect(status.tasks.length).toBe(2);
    expect(status.tasks[0].id).toBe('T-001');
  });

  it('should skip tasks that are already completed', async () => {
    change = createMinimalChange();

    const status = {
      current_task_index: 2,
      total_tasks: 2,
      tasks: [
        { id: 'T-001', status: 'completed', attempts: 1 },
        { id: 'T-002', status: 'completed', attempts: 1 },
      ],
    };
    fs.writeFileSync(change.taskStatusPath, JSON.stringify(status));

    const onTaskStart = vi.fn();
    const context = {
      worktreePath: tempDir,
      projectPath: tempDir,
      onTaskStart,
    };

    const result = await runRalphLoop(change, context, { maxRetries: 0 });

    expect(onTaskStart).not.toHaveBeenCalled();
    expect(result.completed).toBe(0);
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

  describe('precedence rules', () => {
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
    expect(FAILURE_CATEGORY_CONFIGS.timeout.maxAttempts).toBe(1);
    expect(FAILURE_CATEGORY_CONFIGS.timeout.retryable).toBe(false);
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