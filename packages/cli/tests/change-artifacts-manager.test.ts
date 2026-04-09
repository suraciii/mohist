import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { ChangeArtifactsManager, type PrdTaskStatus } from '../src/artifacts/change-artifacts-manager';

describe('ChangeArtifactsManager.updateTaskStatus', () => {
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

  function createChangeWithPrd(issueNumber: number, title: string, prd: object) {
    const changeName = `${issueNumber}-${title}`;
    const changePath = path.join(changesDir, changeName);
    fs.mkdirSync(path.join(changePath, 'specs'), { recursive: true });
    fs.writeFileSync(path.join(changePath, 'prd.json'), JSON.stringify(prd, null, 2));
    return changePath;
  }

  it('should return false when prd.json does not exist', () => {
    const result = manager.updateTaskStatus(999, 'T-001', { status: 'in_progress' });
    expect(result).toBe(false);
  });

  it('should return false when task not found in prd.json', () => {
    createChangeWithPrd(42, 'test-change', {
      tasks: [{ id: 'T-001', title: 'Task 1', description: 'Do thing' }]
    });
    const result = manager.updateTaskStatus(42, 'T-999', { status: 'in_progress' });
    expect(result).toBe(false);
  });

  it('should update task status to in_progress with timestamp', () => {
    createChangeWithPrd(42, 'test-change', {
      tasks: [{ id: 'T-001', title: 'Task 1', description: 'Do thing' }]
    });
    const startedAt = new Date().toISOString();
    const result = manager.updateTaskStatus(42, 'T-001', { status: 'in_progress', startedAt });
    expect(result).toBe(true);

    const prd = manager.readPrd(42);
    expect(prd!.tasks[0].status).toBe('in_progress');
    expect(prd!.tasks[0].startedAt).toBe(startedAt);
  });

  it('should update task status to completed with completedAt timestamp', () => {
    createChangeWithPrd(42, 'test-change', {
      tasks: [{ id: 'T-001', title: 'Task 1', description: 'Do thing' }]
    });
    const completedAt = new Date().toISOString();
    const result = manager.updateTaskStatus(42, 'T-001', { status: 'completed', completedAt });
    expect(result).toBe(true);

    const prd = manager.readPrd(42);
    expect(prd!.tasks[0].status).toBe('completed');
    expect(prd!.tasks[0].completedAt).toBe(completedAt);
  });

  it('should update task status to failed with error message', () => {
    createChangeWithPrd(42, 'test-change', {
      tasks: [{ id: 'T-001', title: 'Task 1', description: 'Do thing' }]
    });
    const errorMsg = 'Missing implementation';
    const result = manager.updateTaskStatus(42, 'T-001', { status: 'failed', error: errorMsg });
    expect(result).toBe(true);

    const prd = manager.readPrd(42);
    expect(prd!.tasks[0].status).toBe('failed');
    expect(prd!.tasks[0].error).toBe(errorMsg);
  });

  it('should track attempt counts', () => {
    createChangeWithPrd(42, 'test-change', {
      tasks: [{ id: 'T-001', title: 'Task 1', description: 'Do thing' }]
    });
    manager.updateTaskStatus(42, 'T-001', { status: 'in_progress' });
    manager.updateTaskStatus(42, 'T-001', { status: 'failed', attempts: 2 });
    manager.updateTaskStatus(42, 'T-001', { status: 'in_progress' });
    manager.updateTaskStatus(42, 'T-001', { status: 'completed', attempts: 4 });

    const prd = manager.readPrd(42);
    expect(prd!.tasks[0].attempts).toBe(4);
  });

  it('should update only status when no optional fields provided', () => {
    createChangeWithPrd(42, 'test-change', {
      tasks: [{ id: 'T-001', title: 'Task 1', description: 'Do thing' }]
    });
    const result = manager.updateTaskStatus(42, 'T-001', { status: 'pending' });
    expect(result).toBe(true);

    const prd = manager.readPrd(42);
    expect(prd!.tasks[0].status).toBe('pending');
    expect(prd!.tasks[0].startedAt).toBeUndefined();
    expect(prd!.tasks[0].completedAt).toBeUndefined();
  });

  it('should handle multiple tasks and update correct one', () => {
    createChangeWithPrd(42, 'test-change', {
      tasks: [
        { id: 'T-001', title: 'Task 1', description: 'Do thing 1' },
        { id: 'T-002', title: 'Task 2', description: 'Do thing 2' },
        { id: 'T-003', title: 'Task 3', description: 'Do thing 3' }
      ]
    });
    manager.updateTaskStatus(42, 'T-002', { status: 'completed' });

    const prd = manager.readPrd(42);
    expect(prd!.tasks[0].status).toBeUndefined();
    expect(prd!.tasks[1].status).toBe('completed');
    expect(prd!.tasks[2].status).toBeUndefined();
  });

  it('should preserve existing task fields when updating status', () => {
    createChangeWithPrd(42, 'test-change', {
      tasks: [{
        id: 'T-001',
        title: 'Task 1',
        description: 'Do thing',
        order: 1,
        acceptance_criteria: ['Crit 1']
      }]
    });
    manager.updateTaskStatus(42, 'T-001', { status: 'in_progress' });

    const prd = manager.readPrd(42);
    expect(prd!.tasks[0].title).toBe('Task 1');
    expect(prd!.tasks[0].order).toBe(1);
    expect(prd!.tasks[0].acceptance_criteria).toEqual(['Crit 1']);
  });

  it('should update error when task is completed after failure', () => {
    createChangeWithPrd(42, 'test-change', {
      tasks: [{ id: 'T-001', title: 'Task 1', description: 'Do thing' }]
    });
    manager.updateTaskStatus(42, 'T-001', { status: 'failed', error: 'Some error' });
    manager.updateTaskStatus(42, 'T-001', { status: 'completed', error: 'Resolved' });

    const prd = manager.readPrd(42);
    expect(prd!.tasks[0].status).toBe('completed');
    expect(prd!.tasks[0].error).toBe('Resolved');
  });

  it('should return false when issueNumber is invalid', () => {
    createChangeWithPrd(42, 'test-change', {
      tasks: [{ id: 'T-001', title: 'Task 1', description: 'Do thing' }]
    });
    const result = manager.updateTaskStatus(0, 'T-001', { status: 'completed' });
    expect(result).toBe(false);
  });
});

describe('ChangeArtifactsManager.readPrd', () => {
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
    expect(manager.readPrd(999)).toBeNull();
  });

  it('should return null when prd.json does not exist in change directory', () => {
    const changesDir = path.join(tmpDir, '.mohist', 'changes');
    fs.mkdirSync(path.join(changesDir, '42-test'), { recursive: true });
    expect(manager.readPrd(42)).toBeNull();
  });

  it('should parse valid prd.json', () => {
    const changesDir = path.join(tmpDir, '.mohist', 'changes');
    const changePath = path.join(changesDir, '42-test');
    fs.mkdirSync(path.join(changePath, 'specs'), { recursive: true });
    fs.writeFileSync(path.join(changePath, 'prd.json'), JSON.stringify({
      tasks: [{ id: 'T-001', title: 'Test', description: 'Test task' }]
    }));

    const prd = manager.readPrd(42);
    expect(prd).not.toBeNull();
    expect(prd!.tasks[0].id).toBe('T-001');
  });

  it('should return null for invalid JSON', () => {
    const changesDir = path.join(tmpDir, '.mohist', 'changes');
    const changePath = path.join(changesDir, '42-test');
    fs.mkdirSync(path.join(changePath, 'specs'), { recursive: true });
    fs.writeFileSync(path.join(changePath, 'prd.json'), '{ invalid json');

    expect(manager.readPrd(42)).toBeNull();
  });
});