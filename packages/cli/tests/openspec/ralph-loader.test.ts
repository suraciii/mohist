import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { RalphTaskLoader } from '../../src/openspec/ralph/loader';
import type { OpenSpecChange } from '../../src/openspec/detector';

describe('RalphTaskLoader', () => {
  let tempDir: string;
  let change: OpenSpecChange;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-loader-test-'));
    const changeDir = path.join(tempDir, 'openspec', 'changes', 'test');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.mkdirSync(path.join(changeDir, 'session-memories'), { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'proposal.md'), '# Test Proposal');
    fs.writeFileSync(path.join(changeDir, 'design.md'), '# Test Design');

    change = {
      changePath: changeDir,
      tasksPath: path.join(changeDir, 'tasks.json'),
      sessionMemoriesPath: path.join(changeDir, 'session-memories'),
      proposalPath: path.join(changeDir, 'proposal.md'),
      designPath: path.join(changeDir, 'design.md'),
      specsPath: path.join(changeDir, 'specs'),
    };
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  function writeTasks(tasks: object[]): void {
    fs.writeFileSync(change.tasksPath, JSON.stringify({ version: 1, tasks }));
  }

  describe('valid loading', () => {
    it('returns empty list when tasks.json does not exist', () => {
      const loader = new RalphTaskLoader();
      const result = loader.load(change);
      expect(result.tasks).toHaveLength(0);
      expect(result.validation.valid).toBe(true);
    });

    it('returns empty list for empty tasks array', () => {
      writeTasks([]);
      const loader = new RalphTaskLoader();
      const result = loader.load(change);
      expect(result.tasks).toHaveLength(0);
    });

    it('applies normalization defaults to tasks', () => {
      writeTasks([
        { id: 'T-001', title: 'A', description: 'desc' },
      ]);
      const loader = new RalphTaskLoader();
      const result = loader.load(change);
      expect(result.tasks).toHaveLength(1);
      expect(result.tasks[0].task.attempts).toBe(0);
      expect(result.tasks[0].task.passes).toBe(false);
      expect(result.tasks[0].task.order).toBe(999999);
      expect(result.tasks[0].task.error).toBeNull();
    });

    it('sorts tasks by order ascending', () => {
      writeTasks([
        { id: 'T-003', order: 3, title: 'Third', description: 'desc' },
        { id: 'T-001', order: 1, title: 'First', description: 'desc' },
        { id: 'T-002', order: 2, title: 'Second', description: 'desc' },
      ]);
      const loader = new RalphTaskLoader();
      const result = loader.load(change);
      expect(result.tasks.map(t => t.task.id)).toEqual(['T-001', 'T-002', 'T-003']);
    });

    it('preserves task order when all tasks have same order', () => {
      writeTasks([
        { id: 'T-001', order: 1, title: 'A', description: 'desc' },
        { id: 'T-002', order: 1, title: 'B', description: 'desc' },
      ]);
      const loader = new RalphTaskLoader();
      const result = loader.load(change);
      expect(result.tasks).toHaveLength(2);
    });

    it('returns totalTasks equal to loaded task count', () => {
      writeTasks([
        { id: 'T-001', order: 1, title: 'A', description: 'desc' },
        { id: 'T-002', order: 2, title: 'B', description: 'desc' },
      ]);
      const loader = new RalphTaskLoader();
      const result = loader.load(change);
      expect(result.tasks[0].totalTasks).toBe(2);
      expect(result.tasks[1].totalTasks).toBe(2);
    });

  });

  describe('missing dependency detection', () => {
    it('fails validation when dependsOn references non-existent task', () => {
      writeTasks([
        { id: 'T-001', order: 1, title: 'A', description: 'desc', dependsOn: ['T-999'] },
      ]);
      const loader = new RalphTaskLoader();
      const result = loader.load(change);
      expect(result.validation.valid).toBe(false);
      expect(result.validation.errors.some(e => e.includes('T-999'))).toBe(true);
    });

    it('fails validation when dependsOn references higher-order task', () => {
      writeTasks([
        { id: 'T-001', order: 1, title: 'A', description: 'desc', dependsOn: ['T-002'] },
        { id: 'T-002', order: 2, title: 'B', description: 'desc' },
      ]);
      const loader = new RalphTaskLoader();
      const result = loader.load(change);
      expect(result.validation.valid).toBe(false);
      expect(result.validation.errors.some(e => e.includes('lower or equal order'))).toBe(true);
    });

    it('passes validation for valid dependsOn chain', () => {
      writeTasks([
        { id: 'T-001', order: 1, title: 'A', description: 'desc', dependsOn: [] },
        { id: 'T-002', order: 2, title: 'B', description: 'desc', dependsOn: ['T-001'] },
        { id: 'T-003', order: 3, title: 'C', description: 'desc', dependsOn: ['T-002'] },
      ]);
      const loader = new RalphTaskLoader();
      const result = loader.load(change);
      expect(result.validation.valid).toBe(true);
      expect(result.validation.errors).toHaveLength(0);
    });
  });

  describe('circular dependency detection', () => {
    it('fails validation for direct circular dependency', () => {
      writeTasks([
        { id: 'T-001', order: 1, title: 'A', description: 'desc', dependsOn: ['T-002'] },
        { id: 'T-002', order: 2, title: 'B', description: 'desc', dependsOn: ['T-001'] },
      ]);
      const loader = new RalphTaskLoader();
      const result = loader.load(change);
      expect(result.validation.valid).toBe(false);
      expect(result.validation.errors.some(e => e.toLowerCase().includes('circular'))).toBe(true);
    });

    it('fails validation for indirect circular dependency', () => {
      writeTasks([
        { id: 'T-001', order: 1, title: 'A', description: 'desc', dependsOn: ['T-002'] },
        { id: 'T-002', order: 2, title: 'B', description: 'desc', dependsOn: ['T-003'] },
        { id: 'T-003', order: 3, title: 'C', description: 'desc', dependsOn: ['T-001'] },
      ]);
      const loader = new RalphTaskLoader();
      const result = loader.load(change);
      expect(result.validation.valid).toBe(false);
      expect(result.validation.errors.some(e => e.toLowerCase().includes('circular'))).toBe(true);
    });
  });

  describe('aggregate-mode progress reset', () => {
    it('resets passes=false and error=null when ignoreTaskFileProgress=true', () => {
      writeTasks([
        { id: 'T-001', order: 1, title: 'A', description: 'desc', passes: true, attempts: 3, error: 'previous error' },
      ]);
      const loader = new RalphTaskLoader();
      const result = loader.load(change, { ignoreTaskFileProgress: true });
      expect(result.tasks[0].task.passes).toBe(false);
      expect(result.tasks[0].task.error).toBeNull();
    });

    it('preserves passes=true when ignoreTaskFileProgress=false', () => {
      writeTasks([
        { id: 'T-001', order: 1, title: 'A', description: 'desc', passes: true, attempts: 3 },
      ]);
      const loader = new RalphTaskLoader();
      const result = loader.load(change, { ignoreTaskFileProgress: false });
      expect(result.tasks[0].task.passes).toBe(true);
    });

    it('preserves attempts count when ignoreTaskFileProgress=true', () => {
      writeTasks([
        { id: 'T-001', order: 1, title: 'A', description: 'desc', passes: false, attempts: 5 },
      ]);
      const loader = new RalphTaskLoader();
      const result = loader.load(change, { ignoreTaskFileProgress: true });
      expect(result.tasks[0].task.attempts).toBe(5);
    });
  });

  describe('RalphLoadedTask shape', () => {
    it('includes task, totalTasks, and change in each loaded task', () => {
      writeTasks([
        { id: 'T-001', order: 1, title: 'A', description: 'desc' },
      ]);
      const loader = new RalphTaskLoader();
      const result = loader.load(change);
      expect(result.tasks[0].task.id).toBe('T-001');
      expect(result.tasks[0].totalTasks).toBe(1);
      expect(result.tasks[0].change).toBe(change);
    });
  });
});
