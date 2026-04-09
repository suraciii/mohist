import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import {
  createUpdateTaskStatusTool,
  createGetTaskStatusTool,
} from '../src/tools/task-status';
import type { TaskStatusFile } from '../src/tools/task-status';

describe('createUpdateTaskStatusTool', () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  async function executeUpdate(params: {
    change_path: string;
    task_id: string;
    status: 'pending' | 'in_progress' | 'completed' | 'failed' | 'skipped';
    error?: string;
    all_task_ids?: string[];
  }) {
    const tool = createUpdateTaskStatusTool({ projectPath: tmpDir });
    const parsed = tool.definition.parameters.safeParse(params);
    if (!parsed.success) {
      return `Validation error: ${parsed.error.issues.map((i) => i.message).join(', ')}`;
    }
    return tool.definition.execute(parsed.data);
  }

  function readStatusFile(changePath: string): TaskStatusFile | null {
    const filePath = path.join(tmpDir, changePath, 'task-status.json');
    if (!fs.existsSync(filePath)) return null;
    return JSON.parse(fs.readFileSync(filePath, 'utf-8'));
  }

  it('should create task-status.json when all_task_ids provided', async () => {
    const changePath = '.mohist-specs/changes/42-test';
    fs.mkdirSync(path.join(tmpDir, changePath), { recursive: true });

    const result = await executeUpdate({
      change_path: changePath,
      task_id: 'T-001',
      status: 'in_progress',
      all_task_ids: ['T-001', 'T-002', 'T-003'],
    });

    expect(result).toContain('T-001');
    expect(result).toContain('in_progress');

    const status = readStatusFile(changePath)!;
    expect(status).toBeDefined();
    expect(status.total_tasks).toBe(3);
    expect(status.tasks[0].id).toBe('T-001');
    expect(status.tasks[0].status).toBe('in_progress');
    expect(status.tasks[0].attempts).toBe(1);
    expect(status.tasks[1].status).toBe('pending');
    expect(status.tasks[2].status).toBe('pending');
  });

  it('should update existing task status and increment attempts', async () => {
    const changePath = '.mohist-specs/changes/42-test';
    fs.mkdirSync(path.join(tmpDir, changePath), { recursive: true });

    await executeUpdate({
      change_path: changePath,
      task_id: 'T-001',
      status: 'in_progress',
      all_task_ids: ['T-001', 'T-002'],
    });

    await executeUpdate({
      change_path: changePath,
      task_id: 'T-001',
      status: 'completed',
    });

    const status = readStatusFile(changePath)!;
    expect(status.tasks[0].status).toBe('completed');
    expect(status.tasks[0].attempts).toBe(2);
  });

  it('should store error when status is failed', async () => {
    const changePath = '.mohist-specs/changes/42-test';
    fs.mkdirSync(path.join(tmpDir, changePath), { recursive: true });

    await executeUpdate({
      change_path: changePath,
      task_id: 'T-001',
      status: 'in_progress',
      all_task_ids: ['T-001'],
    });

    const result = await executeUpdate({
      change_path: changePath,
      task_id: 'T-001',
      status: 'failed',
      error: 'Missing backend validation',
    });

    expect(result).toContain('failed');

    const status = readStatusFile(changePath)!;
    expect(status.tasks[0].status).toBe('failed');
    expect(status.tasks[0].error).toBe('Missing backend validation');
    expect(status.tasks[0].attempts).toBe(2);
  });

  it('should clear error when task completed after failure', async () => {
    const changePath = '.mohist-specs/changes/42-test';
    fs.mkdirSync(path.join(tmpDir, changePath), { recursive: true });

    await executeUpdate({
      change_path: changePath,
      task_id: 'T-001',
      status: 'failed',
      error: 'Some error',
      all_task_ids: ['T-001'],
    });

    await executeUpdate({
      change_path: changePath,
      task_id: 'T-001',
      status: 'completed',
    });

    const status = readStatusFile(changePath)!;
    expect(status.tasks[0].status).toBe('completed');
    expect(status.tasks[0].error).toBeUndefined();
  });

  it('should update current_task_index to next pending task', async () => {
    const changePath = '.mohist-specs/changes/42-test';
    fs.mkdirSync(path.join(tmpDir, changePath), { recursive: true });

    await executeUpdate({
      change_path: changePath,
      task_id: 'T-001',
      status: 'completed',
      all_task_ids: ['T-001', 'T-002', 'T-003'],
    });

    const status = readStatusFile(changePath)!;
    expect(status.current_task_index).toBe(1);

    await executeUpdate({
      change_path: changePath,
      task_id: 'T-002',
      status: 'completed',
    });

    const status2 = readStatusFile(changePath)!;
    expect(status2.current_task_index).toBe(2);
  });

  it('should set current_task_index past end when all tasks done', async () => {
    const changePath = '.mohist-specs/changes/42-test';
    fs.mkdirSync(path.join(tmpDir, changePath), { recursive: true });

    await executeUpdate({
      change_path: changePath,
      task_id: 'T-001',
      status: 'completed',
      all_task_ids: ['T-001'],
    });

    const status = readStatusFile(changePath)!;
    expect(status.current_task_index).toBe(1);
  });

  it('should skip task and clear error', async () => {
    const changePath = '.mohist-specs/changes/42-test';
    fs.mkdirSync(path.join(tmpDir, changePath), { recursive: true });

    await executeUpdate({
      change_path: changePath,
      task_id: 'T-001',
      status: 'failed',
      error: 'Unfixable',
      all_task_ids: ['T-001', 'T-002'],
    });

    await executeUpdate({
      change_path: changePath,
      task_id: 'T-001',
      status: 'skipped',
    });

    const status = readStatusFile(changePath)!;
    expect(status.tasks[0].status).toBe('skipped');
    expect(status.tasks[0].error).toBeUndefined();
  });

  it('should return error when change_path is outside project directory', async () => {
    const result = await executeUpdate({
      change_path: '../../etc',
      task_id: 'T-001',
      status: 'completed',
    });

    expect(result).toBe('Error: change_path is outside the project directory');
  });

  it('should return error when task-status.json missing and no all_task_ids', async () => {
    const changePath = '.mohist-specs/changes/42-test';
    fs.mkdirSync(path.join(tmpDir, changePath), { recursive: true });

    const result = await executeUpdate({
      change_path: changePath,
      task_id: 'T-001',
      status: 'completed',
    });

    expect(result).toContain('all_task_ids');
  });

  it('should return error when task_id not found without all_task_ids', async () => {
    const changePath = '.mohist-specs/changes/42-test';
    fs.mkdirSync(path.join(tmpDir, changePath), { recursive: true });

    await executeUpdate({
      change_path: changePath,
      task_id: 'T-001',
      status: 'completed',
      all_task_ids: ['T-001'],
    });

    const result = await executeUpdate({
      change_path: changePath,
      task_id: 'T-999',
      status: 'completed',
    });

    expect(result).toContain('not found');
  });

  it('should add new task when all_task_ids provided with unknown task_id', async () => {
    const changePath = '.mohist-specs/changes/42-test';
    fs.mkdirSync(path.join(tmpDir, changePath), { recursive: true });

    await executeUpdate({
      change_path: changePath,
      task_id: 'T-001',
      status: 'completed',
      all_task_ids: ['T-001'],
    });

    const result = await executeUpdate({
      change_path: changePath,
      task_id: 'T-002',
      status: 'in_progress',
      all_task_ids: ['T-001', 'T-002'],
    });

    expect(result).toContain('T-002');

    const status = readStatusFile(changePath)!;
    expect(status.total_tasks).toBe(2);
    expect(status.tasks[1].id).toBe('T-002');
    expect(status.tasks[1].status).toBe('in_progress');
  });

  it('should return error when change directory does not exist', async () => {
    const result = await executeUpdate({
      change_path: '.mohist-specs/changes/nonexistent',
      task_id: 'T-001',
      status: 'completed',
      all_task_ids: ['T-001'],
    });

    expect(result).toContain('does not exist');
  });

  it('should reject invalid status', () => {
    const tool = createUpdateTaskStatusTool({ projectPath: tmpDir });
    const result = tool.definition.parameters.safeParse({
      change_path: 'test',
      task_id: 'T-001',
      status: 'invalid_status',
    });

    expect(result.success).toBe(false);
  });

  it('should reject extra parameters', () => {
    const tool = createUpdateTaskStatusTool({ projectPath: tmpDir });
    const result = tool.definition.parameters.safeParse({
      change_path: 'test',
      task_id: 'T-001',
      status: 'completed',
      extra: 'not allowed',
    });

    expect(result.success).toBe(false);
  });

  it('should support all valid statuses', async () => {
    const changePath = '.mohist-specs/changes/42-test';
    fs.mkdirSync(path.join(tmpDir, changePath), { recursive: true });
    const statuses: Array<'pending' | 'in_progress' | 'completed' | 'failed' | 'skipped'> = [
      'pending', 'in_progress', 'completed', 'failed', 'skipped',
    ];

    for (const status of statuses) {
      const tool = createUpdateTaskStatusTool({ projectPath: tmpDir });
      const result = tool.definition.parameters.safeParse({
        change_path: changePath,
        task_id: 'T-001',
        status,
        all_task_ids: ['T-001'],
      });
      expect(result.success).toBe(true);
    }
  });
});

describe('createGetTaskStatusTool', () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  async function executeGet(params: { change_path: string }) {
    const tool = createGetTaskStatusTool({ projectPath: tmpDir });
    const parsed = tool.definition.parameters.safeParse(params);
    if (!parsed.success) {
      return `Validation error: ${parsed.error.issues.map((i) => i.message).join(', ')}`;
    }
    return tool.definition.execute(parsed.data);
  }

  function writeStatusFile(changePath: string, data: TaskStatusFile) {
    const dir = path.join(tmpDir, changePath);
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'task-status.json'), JSON.stringify(data, null, 2));
  }

  it('should read and format task status', async () => {
    const changePath = '.mohist-specs/changes/42-test';
    writeStatusFile(changePath, {
      current_task_index: 2,
      total_tasks: 3,
      tasks: [
        { id: 'T-001', status: 'completed', attempts: 1 },
        { id: 'T-002', status: 'completed', attempts: 1 },
        { id: 'T-003', status: 'failed', attempts: 3, error: 'Missing backend validation' },
      ],
    });

    const result = await executeGet({ change_path: changePath });

    expect(result).toContain('Task Status');
    expect(result).toContain('Current Task Index: 2');
    expect(result).toContain('Total Tasks: 3');
    expect(result).toContain('2 completed');
    expect(result).toContain('1 failed');
    expect(result).toContain('T-001: completed');
    expect(result).toContain('T-002: completed');
    expect(result).toContain('T-003: failed');
    expect(result).toContain('Missing backend validation');
  });

  it('should show next task information', async () => {
    const changePath = '.mohist-specs/changes/42-test';
    writeStatusFile(changePath, {
      current_task_index: 1,
      total_tasks: 3,
      tasks: [
        { id: 'T-001', status: 'completed', attempts: 1 },
        { id: 'T-002', status: 'pending', attempts: 0 },
        { id: 'T-003', status: 'pending', attempts: 0 },
      ],
    });

    const result = await executeGet({ change_path: changePath });

    expect(result).toContain('Next task: T-002');
  });

  it('should return error when task-status.json not found', async () => {
    const changePath = '.mohist-specs/changes/nonexistent';

    const result = await executeGet({ change_path: changePath });

    expect(result).toContain('not found');
  });

  it('should return error when change_path is outside project directory', async () => {
    const result = await executeGet({ change_path: '../../etc' });

    expect(result).toBe('Error: change_path is outside the project directory');
  });

  it('should handle all status types in summary', async () => {
    const changePath = '.mohist-specs/changes/42-test';
    writeStatusFile(changePath, {
      current_task_index: 5,
      total_tasks: 5,
      tasks: [
        { id: 'T-001', status: 'completed', attempts: 1 },
        { id: 'T-002', status: 'completed', attempts: 2 },
        { id: 'T-003', status: 'failed', attempts: 3, error: 'err' },
        { id: 'T-004', status: 'skipped', attempts: 1 },
        { id: 'T-005', status: 'in_progress', attempts: 1 },
      ],
    });

    const result = await executeGet({ change_path: changePath });

    expect(result).toContain('2 completed');
    expect(result).toContain('1 failed');
    expect(result).toContain('1 in_progress');
    expect(result).toContain('1 skipped');
  });

  it('should reject extra parameters', () => {
    const tool = createGetTaskStatusTool({ projectPath: tmpDir });
    const result = tool.definition.parameters.safeParse({
      change_path: 'test',
      extra: 'not allowed',
    });

    expect(result.success).toBe(false);
  });

  it('should return error for invalid JSON in task-status.json', async () => {
    const changePath = '.mohist-specs/changes/42-test';
    const dir = path.join(tmpDir, changePath);
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'task-status.json'), 'not valid json');

    const result = await executeGet({ change_path: changePath });

    expect(result).toContain('invalid JSON');
  });
});
