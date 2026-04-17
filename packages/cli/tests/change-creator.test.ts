import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { createChange } from '../src/openspec/change-creator';

describe('createChange', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  it('should create new change with isNew=true', () => {
    const result = createChange(tempDir, 42, 'Fix bug', 'issue-42');
    expect(result.isNew).toBe(true);
    expect(result.changeName).toBe('42-fix-bug');
    expect(fs.existsSync(result.changePath)).toBe(true);
    expect(fs.existsSync(path.join(result.changePath, 'proposal.md'))).toBe(true);
    expect(fs.existsSync(path.join(result.changePath, 'design.md'))).toBe(true);
    expect(fs.existsSync(path.join(result.changePath, 'specs'))).toBe(true);
    expect(fs.existsSync(path.join(result.changePath, 'session-memories'))).toBe(true);
    expect(fs.existsSync(path.join(result.changePath, '.change.json'))).toBe(true);
  });

  it('should set isNew=true when force=true and existing change exists', () => {
    const first = createChange(tempDir, 42, 'Fix bug', 'issue-42');
    expect(first.isNew).toBe(true);

    const forced = createChange(tempDir, 42, 'Fix bug', 'issue-42', true);
    expect(forced.isNew).toBe(true);
    expect(forced.changeName).toBe('42-fix-bug');
  });

  it('should delete old directory when force=true', () => {
    const first = createChange(tempDir, 42, 'Fix bug', 'issue-42');
    const markerPath = path.join(first.changePath, 'marker.txt');
    fs.writeFileSync(markerPath, 'old');

    const forced = createChange(tempDir, 42, 'Fix bug', 'issue-42', true);
    expect(fs.existsSync(markerPath)).toBe(false);
    expect(fs.existsSync(forced.changePath)).toBe(true);
  });

  it('should create versioned change when duplicate exists', () => {
    createChange(tempDir, 42, 'Fix bug', 'issue-42');
    const second = createChange(tempDir, 42, 'Fix bug', 'issue-42');
    expect(second.isNew).toBe(true);
    expect(second.changeName).toBe('42-fix-bug-v2');
  });

  it('should find correct next version when gaps exist', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    fs.mkdirSync(changesDir, { recursive: true });
    fs.mkdirSync(path.join(changesDir, '42-fix-bug'));
    fs.mkdirSync(path.join(changesDir, '42-fix-bug-v3'));

    const result = createChange(tempDir, 42, 'Fix bug', 'issue-42');
    expect(result.changeName).toBe('42-fix-bug-v4');
  });

  it('should not match different slugs in findNextVersion', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    fs.mkdirSync(changesDir, { recursive: true });
    fs.mkdirSync(path.join(changesDir, '42-fix-bug-view'));
    fs.mkdirSync(path.join(changesDir, '42-fix-bug-extra'));

    const result = createChange(tempDir, 42, 'Fix bug', 'issue-42');
    expect(result.changeName).toBe('42-fix-bug');
  });

  it('should not match different slugs as versions', () => {
    const changesDir = path.join(tempDir, 'openspec', 'changes');
    fs.mkdirSync(changesDir, { recursive: true });
    fs.mkdirSync(path.join(changesDir, '42-fix-bug'));
    fs.mkdirSync(path.join(changesDir, '42-fix-bug-view'));

    const result = createChange(tempDir, 42, 'Fix bug', 'issue-42');
    expect(result.changeName).toBe('42-fix-bug-v2');
    expect(result.changeName).not.toBe('42-fix-bug-view-v2');
  });

  it('should handle sequential version creation correctly', () => {
    const r1 = createChange(tempDir, 42, 'Fix', 'issue-42');
    expect(r1.changeName).toBe('42-fix');

    const r2 = createChange(tempDir, 42, 'Fix', 'issue-42');
    expect(r2.changeName).toBe('42-fix-v2');

    const r3 = createChange(tempDir, 42, 'Fix', 'issue-42');
    expect(r3.changeName).toBe('42-fix-v3');
  });

  it('should preserve existing proposal.md and design.md content', () => {
    const first = createChange(tempDir, 42, 'Fix bug', 'issue-42');
    fs.writeFileSync(path.join(first.changePath, 'proposal.md'), 'My proposal');
    fs.writeFileSync(path.join(first.changePath, 'design.md'), 'My design');

    const second = createChange(tempDir, 42, 'Fix bug', 'issue-42');
    expect(fs.readFileSync(path.join(second.changePath, 'proposal.md'), 'utf8')).toBe('');
    expect(fs.readFileSync(path.join(second.changePath, 'design.md'), 'utf8')).toBe('');
  });

  it('should write correct metadata', () => {
    const result = createChange(tempDir, 42, 'Fix bug', 'issue-42-id');
    const metadata = JSON.parse(
      fs.readFileSync(path.join(result.changePath, '.change.json'), 'utf8'),
    );
    expect(metadata.name).toBe('42-fix-bug');
    expect(metadata.issue_id).toBe('issue-42-id');
    expect(metadata.issue_number).toBe(42);
    expect(metadata.status).toBe('planning');
  });
});
