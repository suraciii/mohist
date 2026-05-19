import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { ChangeArtifactsManager } from '../src/artifacts/change-artifacts-manager';
import { Stage } from '../src/types';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { CommentRepo } from '../src/db/comment-repo';
import { IssueService } from '../src/services/issue-service';

function setupChangeDir(tmpDir: string, issueNumber: number, slug: string): string {
  const changesDir = path.join(tmpDir, 'openspec', 'changes');
  const changeName = `${issueNumber}-${slug}`;
  const changePath = path.join(changesDir, changeName);
  fs.mkdirSync(path.join(changePath, 'specs'), { recursive: true });
  fs.writeFileSync(path.join(changePath, 'proposal.md'), '# Proposal');
  fs.writeFileSync(path.join(changePath, 'tasks.json'), JSON.stringify({ version: 1, tasks: [] }));
  return changePath;
}

function getArchiveEntries(tmpDir: string): string[] {
  const archiveDir = path.join(tmpDir, 'openspec', 'changes', 'archive');
  if (!fs.existsSync(archiveDir)) return [];
  return fs.readdirSync(archiveDir);
}

describe('ChangeArtifactsManager.archiveChange', () => {
  let tmpDir: string;
  let manager: ChangeArtifactsManager;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-archive-test-'));
    manager = new ChangeArtifactsManager(tmpDir);
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it('should produce correct YYYY-MM-DD-<name> path', async () => {
    setupChangeDir(tmpDir, 42, 'fix-auth');

    await manager.archiveChange(42);

    const entries = getArchiveEntries(tmpDir);
    expect(entries).toHaveLength(1);

    const archivedName = entries[0];
    expect(archivedName).toMatch(/^\d{4}-\d{2}-\d{2}-42-fix-auth$/);

    const archiveDir = path.join(tmpDir, 'openspec', 'changes', 'archive', archivedName);
    expect(fs.existsSync(path.join(archiveDir, 'proposal.md'))).toBe(true);
    expect(fs.existsSync(path.join(archiveDir, 'tasks.json'))).toBe(true);
    expect(fs.existsSync(path.join(archiveDir, 'specs'))).toBe(true);

    const changesDir = path.join(tmpDir, 'openspec', 'changes');
    const activeEntries = fs.readdirSync(changesDir).filter(e => e !== 'archive');
    expect(activeEntries).toHaveLength(0);
  });

  it('should use current local date in prefix', async () => {
    setupChangeDir(tmpDir, 7, 'add-feature');

    await manager.archiveChange(7);

    const entries = getArchiveEntries(tmpDir);
    const now = new Date();
    const expectedPrefix = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
    expect(entries[0]).toMatch(new RegExp(`^${expectedPrefix}-7-add-feature(-v\\d+)?$`));
  });

  it('should throw when change directory not found', async () => {
    await expect(manager.archiveChange(999)).rejects.toThrow('ChangeNotFoundError');
  });

  it('should preserve session-memories in archive', async () => {
    const changePath = setupChangeDir(tmpDir, 10, 'with-memories');
    const memDir = path.join(changePath, 'session-memories');
    fs.mkdirSync(memDir, { recursive: true });
    fs.writeFileSync(path.join(memDir, 'T-001.json'), '{}');

    await manager.archiveChange(10);

    const entries = getArchiveEntries(tmpDir);
    const archivedPath = path.join(tmpDir, 'openspec', 'changes', 'archive', entries[0]);
    expect(fs.existsSync(path.join(archivedPath, 'session-memories', 'T-001.json'))).toBe(true);
  });
});

describe('ChangeArtifactsManager.archiveChange conflict resolution', () => {
  let tmpDir: string;
  let manager: ChangeArtifactsManager;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-archive-test-'));
    manager = new ChangeArtifactsManager(tmpDir);
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it('should add -v2 suffix when archive already exists', async () => {
    const archiveDir = path.join(tmpDir, 'openspec', 'changes', 'archive');
    fs.mkdirSync(archiveDir, { recursive: true });

    const now = new Date();
    const datePrefix = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
    fs.mkdirSync(path.join(archiveDir, `${datePrefix}-42-fix-auth`));

    setupChangeDir(tmpDir, 42, 'fix-auth');

    await manager.archiveChange(42);

    const entries = getArchiveEntries(tmpDir);
    expect(entries).toHaveLength(2);
    expect(entries).toContain(`${datePrefix}-42-fix-auth-v2`);
  });

  it('should increment to -v3, -v4 for further conflicts', async () => {
    const archiveDir = path.join(tmpDir, 'openspec', 'changes', 'archive');
    fs.mkdirSync(archiveDir, { recursive: true });

    const now = new Date();
    const datePrefix = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
    fs.mkdirSync(path.join(archiveDir, `${datePrefix}-42-fix-auth`));
    fs.mkdirSync(path.join(archiveDir, `${datePrefix}-42-fix-auth-v2`));

    setupChangeDir(tmpDir, 42, 'fix-auth');

    await manager.archiveChange(42);

    const entries = getArchiveEntries(tmpDir);
    expect(entries).toHaveLength(3);
    expect(entries).toContain(`${datePrefix}-42-fix-auth-v3`);
  });
});

describe('ChangeArtifactsManager.restoreChange', () => {
  let tmpDir: string;
  let manager: ChangeArtifactsManager;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-archive-test-'));
    manager = new ChangeArtifactsManager(tmpDir);
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it('should find date-prefixed archive and strip prefix on restore', async () => {
    const archiveDir = path.join(tmpDir, 'openspec', 'changes', 'archive');
    const archivedName = '2026-05-01-42-fix-auth';
    const archivedPath = path.join(archiveDir, archivedName);
    fs.mkdirSync(archivedPath, { recursive: true });
    fs.writeFileSync(path.join(archivedPath, 'proposal.md'), '# Archived Proposal');

    await manager.restoreChange(42);

    const restoredPath = path.join(tmpDir, 'openspec', 'changes', '42-fix-auth');
    expect(fs.existsSync(restoredPath)).toBe(true);
    expect(fs.existsSync(path.join(restoredPath, 'proposal.md'))).toBe(true);
    expect(fs.existsSync(archivedPath)).toBe(false);
  });

  it('should restore date-prefixed archive keeping -v2 suffix from conflict resolution', async () => {
    const archiveDir = path.join(tmpDir, 'openspec', 'changes', 'archive');
    const archivedName = '2026-05-01-42-fix-auth-v2';
    const archivedPath = path.join(archiveDir, archivedName);
    fs.mkdirSync(archivedPath, { recursive: true });

    await manager.restoreChange(42);

    const restoredPath = path.join(tmpDir, 'openspec', 'changes', '42-fix-auth-v2');
    expect(fs.existsSync(restoredPath)).toBe(true);
    expect(fs.existsSync(archivedPath)).toBe(false);
  });

  it('should throw when archive directory does not exist', async () => {
    await expect(manager.restoreChange(42)).rejects.toThrow('Archive directory not found');
  });

  it('should throw when archived change not found', async () => {
    const archiveDir = path.join(tmpDir, 'openspec', 'changes', 'archive');
    fs.mkdirSync(archiveDir, { recursive: true });

    await expect(manager.restoreChange(999)).rejects.toThrow('not found');
  });
});

describe('IssueService.performCleanup does not call archiveChange', () => {
  let db: DatabaseManager;
  let issueRepo: IssueRepo;
  let commentRepo: CommentRepo;
  let projectRepo: ProjectRepo;
  let projectId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });
    projectId = project.id;
    issueRepo = new IssueRepo(db);
    commentRepo = new CommentRepo(db);
  });

  afterEach(() => {
    db.close();
  });

  it('should not call archiveChange during performCleanup', async () => {
    const issue = issueRepo.create({ number: 1, projectId, title: 'Test' });
    issueRepo.updateStage(issue.id, Stage.Done);

    const mockWorktreeManager = {
      remove: vi.fn().mockResolvedValue(undefined),
    };

    const archiveSpy = vi.fn().mockResolvedValue(undefined);

    const mockCheckpointRepo = {
      deleteAll: vi.fn(),
    };

    const svc = new IssueService(
      issueRepo,
      commentRepo,
      projectRepo,
      mockWorktreeManager as any,
      undefined,
      mockCheckpointRepo as any,
    );

    const result = await svc.archive(projectId, 1);

    expect(result.issue.archivedAt).toBeDefined();
    expect(mockWorktreeManager.remove).toHaveBeenCalled();
    expect(archiveSpy).not.toHaveBeenCalled();
  });

  it('should still clean worktree and checkpoints without archiving openspec', async () => {
    const issue = issueRepo.create({ number: 1, projectId, title: 'Test' });
    issueRepo.updateStage(issue.id, Stage.Done);

    const mockWorktreeManager = {
      remove: vi.fn().mockResolvedValue(undefined),
    };
    const mockCheckpointRepo = {
      deleteAll: vi.fn(),
    };

    const svc = new IssueService(
      issueRepo,
      commentRepo,
      projectRepo,
      mockWorktreeManager as any,
      undefined,
      mockCheckpointRepo as any,
    );

    await svc.archive(projectId, 1);

    expect(mockWorktreeManager.remove).toHaveBeenCalled();
    expect(mockCheckpointRepo.deleteAll).toHaveBeenCalledWith(1);
  });
});
