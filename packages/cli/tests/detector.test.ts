import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { detectOpenSpecChange } from '../src/openspec/detector';
import type { Issue } from '../src/types';

describe('detectOpenSpecChange', () => {
  let tempDir: string;
  
  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
  });
  
  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });
  
  it('should return null when .mohist-specs/changes does not exist', () => {
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
    const changesDir = path.join(tempDir, '.mohist-specs', 'changes');
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
  
  it('should return null when change exists but prd.json is missing', () => {
    const changesDir = path.join(tempDir, '.mohist-specs', 'changes');
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
  
  it('should return OpenSpecChange when change directory with prd.json exists', () => {
    const changesDir = path.join(tempDir, '.mohist-specs', 'changes');
    const changeDir = path.join(changesDir, '42-test-issue');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'prd.json'), '{}');
    
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
    expect(result?.prdPath).toBe(path.join(changeDir, 'prd.json'));
    expect(result?.taskStatusPath).toBe(path.join(changeDir, 'task-status.json'));
    expect(result?.sessionMemoriesPath).toBe(path.join(changeDir, 'session-memories'));
    expect(result?.proposalPath).toBe(path.join(changeDir, 'proposal.md'));
    expect(result?.designPath).toBe(path.join(changeDir, 'design.md'));
    expect(result?.specsPath).toBe(path.join(changeDir, 'specs'));
  });
  
  it('should find change with slug containing issue title', () => {
    const changesDir = path.join(tempDir, '.mohist-specs', 'changes');
    const changeDir = path.join(changesDir, '42-add-user-authentication');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'prd.json'), '{}');
    
    const issue: Issue = {
      id: 'test-id',
      number: 42,
      title: 'Add User Authentication',
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
});
