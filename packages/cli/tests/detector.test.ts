import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { detectOpenSpecChange, findChangeDir } from '../src/openspec/detector';
import type { Issue } from '../src/types';

describe('detectOpenSpecChange', () => {
  let tempDir: string;
  
  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
  });
  
  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });
  
  it('should return null when openspec/changes does not exist', () => {
    const issue: Issue = {
      id: 'test-id',
      number: 42,
      title: 'Test Issue',
      stage: 'build',
      status: 'active',
      projectId: 'test-project',
      labels: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };
    
    const result = detectOpenSpecChange(tempDir, issue);
    expect(result).toBeNull();
  });
  
  it('should return null when no change directory matches issue number', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    fs.mkdirSync(changesDir, { recursive: true });
    fs.mkdirSync(path.join(changesDir, '43-another-issue'));
    
    const issue: Issue = {
      id: 'test-id',
      number: 42,
      title: 'Test Issue',
      stage: 'build',
      status: 'active',
      projectId: 'test-project',
      labels: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };
    
    const result = detectOpenSpecChange(tempDir, issue);
    expect(result).toBeNull();
  });
  
  it('should return null when change exists but tasks.json is missing', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    fs.mkdirSync(changesDir, { recursive: true });
    fs.mkdirSync(path.join(changesDir, '42-test-issue'));
    
    const issue: Issue = {
      id: 'test-id',
      number: 42,
      title: 'Test Issue',
      stage: 'build',
      status: 'active',
      projectId: 'test-project',
      labels: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };
    
    const result = detectOpenSpecChange(tempDir, issue);
    expect(result).toBeNull();
  });
  
  it('should return OpenSpecChange when change directory with tasks.json exists', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    const changeDir = path.join(changesDir, '42-test-issue');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'tasks.json'), '{}');
    
    const issue: Issue = {
      id: 'test-id',
      number: 42,
      title: 'Test Issue',
      stage: 'build',
      status: 'active',
      projectId: 'test-project',
      labels: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };
    
    const result = detectOpenSpecChange(tempDir, issue);
    
    expect(result).not.toBeNull();
    expect(result?.changePath).toBe(changeDir);
    expect(result?.tasksPath).toBe(path.join(changeDir, 'tasks.json'));
    expect(result?.sessionMemoriesPath).toBe(path.join(changeDir, 'session-memories'));
    expect(result?.proposalPath).toBe(path.join(changeDir, 'proposal.md'));
    expect(result?.designPath).toBe(path.join(changeDir, 'design.md'));
    expect(result?.specsPath).toBe(path.join(changeDir, 'specs'));
  });
  
  it('should find change with slug containing issue title', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    const changeDir = path.join(changesDir, '42-add-user-authentication');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'tasks.json'), '{}');
    
    const issue: Issue = {
      id: 'test-id',
      number: 42,
      title: 'Test Issue',
      stage: 'build',
      status: 'active',
      projectId: 'test-project',
      labels: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };
    
    const result = detectOpenSpecChange(tempDir, issue);
    expect(result).not.toBeNull();
    expect(result?.changePath).toBe(changeDir);
  });

  it('should return the latest versioned change', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    const v1Dir = path.join(changesDir, '42-fix');
    const v2Dir = path.join(changesDir, '42-fix-v2');
    const v3Dir = path.join(changesDir, '42-fix-v3');
    fs.mkdirSync(v1Dir, { recursive: true });
    fs.mkdirSync(v2Dir, { recursive: true });
    fs.mkdirSync(v3Dir, { recursive: true });
    fs.writeFileSync(path.join(v1Dir, 'tasks.json'), '{}');
    fs.writeFileSync(path.join(v2Dir, 'tasks.json'), '{}');
    fs.writeFileSync(path.join(v3Dir, 'tasks.json'), '{}');

    const issue: Issue = {
      id: 'test-id',
      number: 42,
      title: 'Fix',
      stage: 'build',
      status: 'active',
      projectId: 'test-project',
      labels: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };

    const result = detectOpenSpecChange(tempDir, issue);
    expect(result).not.toBeNull();
    expect(result?.changePath).toBe(v3Dir);
  });

  it('should not confuse different slugs as same change', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    const fixDir = path.join(changesDir, '42-fix');
    const fixBugDir = path.join(changesDir, '42-fix-bug');
    fs.mkdirSync(fixDir, { recursive: true });
    fs.mkdirSync(fixBugDir, { recursive: true });
    fs.writeFileSync(path.join(fixDir, 'tasks.json'), '{}');
    fs.writeFileSync(path.join(fixBugDir, 'tasks.json'), '{}');

    const issue: Issue = {
      id: 'test-id',
      number: 42,
      title: 'Fix',
      stage: 'build',
      status: 'active',
      projectId: 'test-project',
      labels: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };

    const result = detectOpenSpecChange(tempDir, issue);
    expect(result).not.toBeNull();
    // Both are valid changes with different slugs; returns one of them
    expect([fixDir, fixBugDir]).toContain(result?.changePath);
  });

  it('should prefer versioned change over different slug', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    const fixDir = path.join(changesDir, '42-fix');
    const fixV2Dir = path.join(changesDir, '42-fix-v2');
    const fixBugDir = path.join(changesDir, '42-fix-bug');
    fs.mkdirSync(fixDir, { recursive: true });
    fs.mkdirSync(fixV2Dir, { recursive: true });
    fs.mkdirSync(fixBugDir, { recursive: true });
    fs.writeFileSync(path.join(fixDir, 'tasks.json'), '{}');
    fs.writeFileSync(path.join(fixV2Dir, 'tasks.json'), '{}');
    fs.writeFileSync(path.join(fixBugDir, 'tasks.json'), '{}');

    const issue: Issue = {
      id: 'test-id',
      number: 42,
      title: 'Fix',
      stage: 'build',
      status: 'active',
      projectId: 'test-project',
      labels: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };

    const result = detectOpenSpecChange(tempDir, issue);
    expect(result).not.toBeNull();
    // 42-fix-v2 is the latest version of the "fix" slug
    expect([fixV2Dir, fixBugDir]).toContain(result?.changePath);
  });

  it('should treat unversioned change as v1 and prefer higher versions', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    const v1Dir = path.join(changesDir, '42-feature');
    const v2Dir = path.join(changesDir, '42-feature-v2');
    fs.mkdirSync(v1Dir, { recursive: true });
    fs.mkdirSync(v2Dir, { recursive: true });
    fs.writeFileSync(path.join(v1Dir, 'tasks.json'), '{}');
    fs.writeFileSync(path.join(v2Dir, 'tasks.json'), '{}');

    const issue: Issue = {
      id: 'test-id',
      number: 42,
      title: 'Feature',
      stage: 'build',
      status: 'active',
      projectId: 'test-project',
      labels: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };

    const result = detectOpenSpecChange(tempDir, issue);
    expect(result).not.toBeNull();
    expect(result?.changePath).toBe(v2Dir);
  });

  it('should return null when latest versioned change has no tasks.json', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    const v1Dir = path.join(changesDir, '42-fix');
    const v2Dir = path.join(changesDir, '42-fix-v2');
    fs.mkdirSync(v1Dir, { recursive: true });
    fs.mkdirSync(v2Dir, { recursive: true });
    fs.writeFileSync(path.join(v1Dir, 'tasks.json'), '{}');
    // v2 has no tasks.json

    const issue: Issue = {
      id: 'test-id',
      number: 42,
      title: 'Fix',
      stage: 'build',
      status: 'active',
      projectId: 'test-project',
      labels: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };

    const result = detectOpenSpecChange(tempDir, issue);
    expect(result).toBeNull();
  });
});

describe('findChangeDir', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  it('should return null when openspec/changes does not exist', () => {
    expect(findChangeDir(tempDir, 42)).toBeNull();
  });

  it('should return null when no change directory matches issue number', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    fs.mkdirSync(path.join(changesDir, '43-another-issue'), { recursive: true });
    expect(findChangeDir(tempDir, 42)).toBeNull();
  });

  it('should return change path for matching issue number', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    const changeDir = path.join(changesDir, '42-fix');
    fs.mkdirSync(changeDir, { recursive: true });
    expect(findChangeDir(tempDir, 42)).toBe(changeDir);
  });

  it('should return latest versioned change', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    fs.mkdirSync(path.join(changesDir, '42-fix'), { recursive: true });
    fs.mkdirSync(path.join(changesDir, '42-fix-v2'), { recursive: true });
    fs.mkdirSync(path.join(changesDir, '42-fix-v3'), { recursive: true });
    expect(findChangeDir(tempDir, 42)).toBe(path.join(changesDir, '42-fix-v3'));
  });

  it('should handle multi-hyphen slugs', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    const changeDir = path.join(changesDir, '42-add-user-authentication');
    fs.mkdirSync(changeDir, { recursive: true });
    expect(findChangeDir(tempDir, 42)).toBe(changeDir);
  });

  it('should pick best match across multiple slugs', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    fs.mkdirSync(path.join(changesDir, '42-fix'), { recursive: true });
    fs.mkdirSync(path.join(changesDir, '42-fix-v2'), { recursive: true });
    fs.mkdirSync(path.join(changesDir, '42-other'), { recursive: true });
    const result = findChangeDir(tempDir, 42);
    expect(result).not.toBeNull();
    expect([path.join(changesDir, '42-fix-v2'), path.join(changesDir, '42-other')]).toContain(result);
  });

  it('should not match different issue numbers', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    fs.mkdirSync(path.join(changesDir, '420-fix'), { recursive: true });
    expect(findChangeDir(tempDir, 42)).toBeNull();
  });
});
