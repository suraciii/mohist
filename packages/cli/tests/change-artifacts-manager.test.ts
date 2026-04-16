import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { ChangeArtifactsManager } from '../src/artifacts/change-artifacts-manager';

describe('ChangeArtifactsManager.updateTaskPasses', () => {
  let tmpDir: string;
  let manager: ChangeArtifactsManager;
  let changesDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
    changesDir = path.join(tmpDir, '.mohist', 'changes');
    fs.mkdirSync(changesDir, { recursive: true });
    manager = new ChangeArtifactsManager(tmpDir);
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  function createChangeWithTasks(issueNumber: number, title: string, tasks: object) {
    const changeName = `${issueNumber}-${title}`;
    const changePath = path.join(changesDir, changeName);
    fs.mkdirSync(path.join(changePath, 'specs'), { recursive: true });
    fs.writeFileSync(path.join(changePath, 'tasks.json'), JSON.stringify(tasks, null, 2));
    return changePath;
  }

  it('should return false when tasks.json does not exist', () => {
    const result = manager.updateTaskPasses(999, 'T-001', true);
    expect(result).toBe(false);
  });

  it('should return false when task not found in tasks.json', () => {
    createChangeWithTasks(42, 'test-change', {
      version: 1,
      tasks: [{ id: 'T-001', title: 'Task 1', description: 'Do thing', order: 1, passes: false, attempts: 0 }]
    });
    const result = manager.updateTaskPasses(42, 'T-999', true);
    expect(result).toBe(false);
  });

  it('should update task passes to true', () => {
    createChangeWithTasks(42, 'test-change', {
      version: 1,
      tasks: [{ id: 'T-001', title: 'Task 1', description: 'Do thing', order: 1, passes: false, attempts: 0 }]
    });
    const result = manager.updateTaskPasses(42, 'T-001', true);
    expect(result).toBe(true);

    const tasks = manager.readTasks(42);
    expect(tasks!.tasks[0].passes).toBe(true);
  });

  it('should update task passes to false with error', () => {
    createChangeWithTasks(42, 'test-change', {
      version: 1,
      tasks: [{ id: 'T-001', title: 'Task 1', description: 'Do thing', order: 1, passes: false, attempts: 0 }]
    });
    const errorMsg = 'Missing implementation';
    const result = manager.updateTaskPasses(42, 'T-001', false, errorMsg);
    expect(result).toBe(true);

    const tasks = manager.readTasks(42);
    expect(tasks!.tasks[0].passes).toBe(false);
    expect(tasks!.tasks[0].error).toBe(errorMsg);
  });

  it('should handle multiple tasks and update correct one', () => {
    createChangeWithTasks(42, 'test-change', {
      version: 1,
      tasks: [
        { id: 'T-001', title: 'Task 1', description: 'Do thing 1', order: 1, passes: false, attempts: 0 },
        { id: 'T-002', title: 'Task 2', description: 'Do thing 2', order: 2, passes: false, attempts: 0 },
        { id: 'T-003', title: 'Task 3', description: 'Do thing 3', order: 3, passes: false, attempts: 0 }
      ]
    });
    manager.updateTaskPasses(42, 'T-002', true);

    const tasks = manager.readTasks(42);
    expect(tasks!.tasks[0].passes).toBe(false);
    expect(tasks!.tasks[1].passes).toBe(true);
    expect(tasks!.tasks[2].passes).toBe(false);
  });

  it('should preserve existing task fields when updating passes', () => {
    createChangeWithTasks(42, 'test-change', {
      version: 1,
      tasks: [{
        id: 'T-001',
        title: 'Task 1',
        description: 'Do thing',
        order: 1,
        acceptanceCriteria: ['Crit 1'],
        passes: false,
        attempts: 0
      }]
    });
    manager.updateTaskPasses(42, 'T-001', true);

    const tasks = manager.readTasks(42);
    expect(tasks!.tasks[0].title).toBe('Task 1');
    expect(tasks!.tasks[0].order).toBe(1);
    expect(tasks!.tasks[0].acceptanceCriteria).toEqual(['Crit 1']);
  });

  it('should update error when task is completed after failure', () => {
    createChangeWithTasks(42, 'test-change', {
      version: 1,
      tasks: [{ id: 'T-001', title: 'Task 1', description: 'Do thing', order: 1, passes: false, attempts: 1 }]
    });
    manager.updateTaskPasses(42, 'T-001', false, 'Some error');
    manager.updateTaskPasses(42, 'T-001', true, null);

    const tasks = manager.readTasks(42);
    expect(tasks!.tasks[0].passes).toBe(true);
    expect(tasks!.tasks[0].error).toBeNull();
  });

  it('should return false when issueNumber is invalid', () => {
    createChangeWithTasks(42, 'test-change', {
      version: 1,
      tasks: [{ id: 'T-001', title: 'Task 1', description: 'Do thing', order: 1, passes: false, attempts: 0 }]
    });
    const result = manager.updateTaskPasses(0, 'T-001', true);
    expect(result).toBe(false);
  });
});

describe('ChangeArtifactsManager.readTasks', () => {
  let tmpDir: string;
  let manager: ChangeArtifactsManager;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
    manager = new ChangeArtifactsManager(tmpDir);
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it('should return null when change directory does not exist', () => {
    expect(manager.readTasks(999)).toBeNull();
  });

  it('should return null when tasks.json does not exist in change directory', () => {
    const changesDir = path.join(tmpDir, '.mohist', 'changes');
    fs.mkdirSync(path.join(changesDir, '42-test'), { recursive: true });
    expect(manager.readTasks(42)).toBeNull();
  });

  it('should parse valid tasks.json', () => {
    const changesDir = path.join(tmpDir, '.mohist', 'changes');
    const changePath = path.join(changesDir, '42-test');
    fs.mkdirSync(path.join(changePath, 'specs'), { recursive: true });
    fs.writeFileSync(path.join(changePath, 'tasks.json'), JSON.stringify({
      version: 1,
      tasks: [{ id: 'T-001', title: 'Test', description: 'Test task', order: 1, passes: false, attempts: 0 }]
    }));

    const tasks = manager.readTasks(42);
    expect(tasks).not.toBeNull();
    expect(tasks!.tasks[0].id).toBe('T-001');
  });

  it('should return null for invalid JSON', () => {
    const changesDir = path.join(tmpDir, '.mohist', 'changes');
    const changePath = path.join(changesDir, '42-test');
    fs.mkdirSync(path.join(changePath, 'specs'), { recursive: true });
    fs.writeFileSync(path.join(changePath, 'tasks.json'), '{ invalid json');

    expect(manager.readTasks(42)).toBeNull();
  });
});
