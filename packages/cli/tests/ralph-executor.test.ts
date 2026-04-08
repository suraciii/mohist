import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { runRalphLoop, sortTasksByOrder, getOrderValue, readTaskStatus, readPrdTasks, RalphExecutor } from '../src/openspec/ralph-executor';
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