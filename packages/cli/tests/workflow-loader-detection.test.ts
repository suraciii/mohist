import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import {
  detectOpenSpecForIssue,
  loadWorkflowWithDetection,
} from '../src/workflow/workflow-loader';

describe('detectOpenSpecForIssue', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-wl-test-'));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  it('should return traditional mode when .mohist-specs does not exist', () => {
    const result = detectOpenSpecForIssue(tempDir, 42);
    expect(result.detected).toBe(false);
    expect(result.mode).toBe('traditional');
  });

  it('should return traditional mode when changes directory is empty', () => {
    fs.mkdirSync(path.join(tempDir, '.mohist-specs', 'changes'), { recursive: true });
    const result = detectOpenSpecForIssue(tempDir, 42);
    expect(result.detected).toBe(false);
    expect(result.mode).toBe('traditional');
  });

  it('should return traditional mode when no change matches issue number', () => {
    const changesDir = path.join(tempDir, '.mohist-specs', 'changes');
    fs.mkdirSync(path.join(changesDir, '43-another-issue'), { recursive: true });
    const result = detectOpenSpecForIssue(tempDir, 42);
    expect(result.detected).toBe(false);
    expect(result.mode).toBe('traditional');
  });

  it('should return detected+traditional when change dir exists but no tasks.json', () => {
    const changesDir = path.join(tempDir, '.mohist-specs', 'changes');
    const changeDir = path.join(changesDir, '42-test-issue');
    fs.mkdirSync(changeDir, { recursive: true });

    const result = detectOpenSpecForIssue(tempDir, 42);
    expect(result.detected).toBe(true);
    expect(result.changePath).toBe(changeDir);
    expect(result.tasksPath).toBeUndefined();
    expect(result.mode).toBe('traditional');
  });

  it('should return openspec mode when tasks.json exists', () => {
    const changesDir = path.join(tempDir, '.mohist-specs', 'changes');
    const changeDir = path.join(changesDir, '42-test-issue');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'tasks.json'), '{}');

    const result = detectOpenSpecForIssue(tempDir, 42);
    expect(result.detected).toBe(true);
    expect(result.changePath).toBe(changeDir);
    expect(result.tasksPath).toBe(path.join(changeDir, 'tasks.json'));
    expect(result.mode).toBe('openspec');
  });

  it('should find change with slug containing issue title', () => {
    const changesDir = path.join(tempDir, '.mohist-specs', 'changes');
    const changeDir = path.join(changesDir, '42-add-user-authentication');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'tasks.json'), '{}');

    const result = detectOpenSpecForIssue(tempDir, 42);
    expect(result.detected).toBe(true);
    expect(result.mode).toBe('openspec');
    expect(result.changePath).toBe(changeDir);
  });

  it('should return latest versioned change using unified findChangeDir', () => {
    const changesDir = path.join(tempDir, '.mohist-specs', 'changes');
    const v1Dir = path.join(changesDir, '42-fix');
    const v2Dir = path.join(changesDir, '42-fix-v2');
    fs.mkdirSync(v1Dir, { recursive: true });
    fs.mkdirSync(v2Dir, { recursive: true });
    fs.writeFileSync(path.join(v2Dir, 'tasks.json'), '{}');

    const result = detectOpenSpecForIssue(tempDir, 42);
    expect(result.detected).toBe(true);
    expect(result.mode).toBe('openspec');
    expect(result.changePath).toBe(v2Dir);
  });

  it('should return detected+traditional for latest version without tasks.json', () => {
    const changesDir = path.join(tempDir, '.mohist-specs', 'changes');
    const v1Dir = path.join(changesDir, '42-fix');
    const v2Dir = path.join(changesDir, '42-fix-v2');
    fs.mkdirSync(v1Dir, { recursive: true });
    fs.mkdirSync(v2Dir, { recursive: true });
    fs.writeFileSync(path.join(v1Dir, 'tasks.json'), '{}');

    const result = detectOpenSpecForIssue(tempDir, 42);
    expect(result.detected).toBe(true);
    expect(result.changePath).toBe(v2Dir);
    expect(result.mode).toBe('traditional');
  });
});

describe('loadWorkflowWithDetection', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-wl-load-'));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  it('should return workflow with traditional openspec when no change exists', () => {
    const result = loadWorkflowWithDetection(tempDir, 42);
    if (typeof result === 'string') {
      throw new Error('Expected config, got string');
    }
    expect(result.stages).toBeDefined();
    expect(result.stages.length).toBeGreaterThan(0);
    expect(result.source).toBe('builtin');
    expect(result.openspec.detected).toBe(false);
    expect(result.openspec.mode).toBe('traditional');
  });

  it('should return workflow with openspec mode when tasks.json exists', () => {
    const changesDir = path.join(tempDir, '.mohist-specs', 'changes');
    const changeDir = path.join(changesDir, '42-test');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'tasks.json'), '{}');

    const result = loadWorkflowWithDetection(tempDir, 42);
    if (typeof result === 'string') {
      throw new Error('Expected config, got string');
    }
    expect(result.openspec.detected).toBe(true);
    expect(result.openspec.mode).toBe('openspec');
    expect(result.openspec.tasksPath).toBe(path.join(changeDir, 'tasks.json'));
  });

  it('should return workflow with detected+traditional when change dir but no tasks', () => {
    const changesDir = path.join(tempDir, '.mohist-specs', 'changes');
    fs.mkdirSync(path.join(changesDir, '42-test'), { recursive: true });

    const result = loadWorkflowWithDetection(tempDir, 42);
    if (typeof result === 'string') {
      throw new Error('Expected config, got string');
    }
    expect(result.openspec.detected).toBe(true);
    expect(result.openspec.mode).toBe('traditional');
  });
});
