import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import http from 'node:http';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { Hono } from 'hono';
import request from 'supertest';
import { Command } from 'commander';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { PipelineCheckpointRepo } from '../src/db/pipeline-checkpoint-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { ConfigService } from '../src/services/config-service';
import { EventBus, AgentRunnerService } from '../src/services';
import { StateManager } from '../src/server/state-manager';
import { createIssueRoutes } from '../src/api/issues';
import { setupIssueCommands } from '../src/cli/commands/issue';
import { Stage, IssueStatus, MergeState } from '../src/types';

function createTestServer(app: Hono): http.Server {
  return http.createServer(async (req, res) => {
    const chunks: Buffer[] = [];
    for await (const chunk of req) chunks.push(chunk);
    const bodyStr = chunks.length > 0 ? Buffer.concat(chunks).toString() : undefined;
    const initHeaders: Record<string, string> = {};
    for (const [key, value] of Object.entries(req.headers)) {
      if (typeof value === 'string') initHeaders[key] = value;
      else if (Array.isArray(value)) initHeaders[key] = value.join(', ');
    }
    const response = await app.fetch(new Request(`http://localhost${req.url}`, {
      method: req.method,
      headers: initHeaders,
      body: bodyStr,
    }));
    res.writeHead(response.status, Object.fromEntries(response.headers.entries()));
    if (response.body) {
      const reader = response.body.getReader();
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        res.write(Buffer.from(value));
      }
    }
    res.end();
  });
}

describe('Migration v16', () => {
  let db: DatabaseManager;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
  });

  afterEach(() => {
    db.close();
  });

  it('should add archived_at column to issues table', () => {
    const columns = db.all<{ name: string }>('PRAGMA table_info(issues)');
    const archivedAtCol = columns.find(col => col.name === 'archived_at');
    expect(archivedAtCol).toBeDefined();
  });

  it('should create index on archived_at', () => {
    const indexes = db.all<{ name: string }>(
      "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='issues'"
    );
    const archivedIndex = indexes.find(idx => idx.name === 'idx_issues_archived');
    expect(archivedIndex).toBeDefined();
  });

  it('should default archived_at to NULL for new issues', () => {
    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });
    const issueRepo = new IssueRepo(db);
    const issue = issueRepo.create({ number: 1, projectId: project.id, title: 'Test' });

    const row = db.get<{ archived_at: string | null }>(
      'SELECT archived_at FROM issues WHERE id = ?',
      [issue.id]
    );
    expect(row?.archived_at).toBeNull();
  });

  it('should have schema version 28', () => {
    const row = db.get<{ value: string }>(
      "SELECT value FROM config WHERE key = 'schema_version'"
    );
    expect(row?.value).toBe('28');
  });
});

describe('IssueRepo archive', () => {
  let db: DatabaseManager;
  let repo: IssueRepo;
  let projectId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    projectId = project.id;
    repo = new IssueRepo(db);
  });

  afterEach(() => {
    db.close();
  });

  describe('archive()', () => {
    it('should set archived_at timestamp', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });
      const before = new Date().toISOString();

      const archived = repo.archive(issue.id);

      expect(archived).not.toBeNull();
      expect(archived!.archivedAt).toBeDefined();
      expect(typeof archived!.archivedAt).toBe('string');
      expect(archived!.archivedAt!.length).toBeGreaterThan(0);
    });

    it('should update updated_at when archiving', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });

      const archived = repo.archive(issue.id);

      expect(archived!.updatedAt).toBeDefined();
      expect(new Date(archived!.updatedAt).getTime()).not.toBeNaN();
    });

    it('should return null for non-existent issue', () => {
      const result = repo.archive('nonexistent-id');
      expect(result).toBeNull();
    });
  });

  describe('unarchive()', () => {
    it('should clear archived_at', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });
      repo.archive(issue.id);

      const unarchived = repo.unarchive(issue.id);

      expect(unarchived).not.toBeNull();
      expect(unarchived!.archivedAt).toBeUndefined();
    });

    it('should update updated_at when unarchiving', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });
      repo.archive(issue.id);

      const unarchived = repo.unarchive(issue.id);

      expect(unarchived!.updatedAt).toBeDefined();
      expect(new Date(unarchived!.updatedAt).getTime()).not.toBeNaN();
    });

    it('should preserve original stage and status after unarchive', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });
      repo.updateStage(issue.id, Stage.Done);
      repo.updateStatus(issue.id, IssueStatus.Completed);
      repo.archive(issue.id);

      const unarchived = repo.unarchive(issue.id);

      expect(unarchived!.stage).toBe(Stage.Done);
      expect(unarchived!.status).toBe(IssueStatus.Completed);
    });

    it('should return null for non-existent issue', () => {
      const result = repo.unarchive('nonexistent-id');
      expect(result).toBeNull();
    });
  });

  describe('findArchived()', () => {
    it('should return only archived issues', () => {
      repo.create({ number: 1, projectId, title: 'Active' });
      const issue2 = repo.create({ number: 2, projectId, title: 'To Archive' });
      repo.archive(issue2.id);

      const archived = repo.findArchived(projectId);

      expect(archived).toHaveLength(1);
      expect(archived[0].number).toBe(2);
    });

    it('should return empty array when no archived issues', () => {
      repo.create({ number: 1, projectId, title: 'Active' });

      const archived = repo.findArchived(projectId);

      expect(archived).toHaveLength(0);
    });
  });

  describe('findAll() filtering', () => {
    it('should exclude archived issues by default', () => {
      const issue1 = repo.create({ number: 1, projectId, title: 'Active' });
      const issue2 = repo.create({ number: 2, projectId, title: 'To Archive' });
      repo.archive(issue2.id);

      const all = repo.findAll({ projectId });

      expect(all).toHaveLength(1);
      expect(all[0].number).toBe(1);
    });

    it('should include archived issues when includeArchived is true', () => {
      const issue1 = repo.create({ number: 1, projectId, title: 'Active' });
      const issue2 = repo.create({ number: 2, projectId, title: 'To Archive' });
      repo.archive(issue2.id);

      const all = repo.findAll({ projectId, includeArchived: true });

      expect(all).toHaveLength(2);
    });

    it('should return only archived issues when archivedOnly is true', () => {
      repo.create({ number: 1, projectId, title: 'Active' });
      const issue2 = repo.create({ number: 2, projectId, title: 'To Archive' });
      repo.archive(issue2.id);

      const archived = repo.findAll({ projectId, archivedOnly: true });

      expect(archived).toHaveLength(1);
      expect(archived[0].number).toBe(2);
    });

    it('should exclude archived from stage filtering by default', () => {
      const issue1 = repo.create({ number: 1, projectId, title: 'Done Active' });
      repo.updateStage(issue1.id, Stage.Done);
      const issue2 = repo.create({ number: 2, projectId, title: 'Done Archived' });
      repo.updateStage(issue2.id, Stage.Done);
      repo.archive(issue2.id);

      const doneIssues = repo.findAll({ projectId, stage: Stage.Done });

      expect(doneIssues).toHaveLength(1);
      expect(doneIssues[0].number).toBe(1);
    });

    it('should include archived in stage filtering when includeArchived is true', () => {
      const issue1 = repo.create({ number: 1, projectId, title: 'Done Active' });
      repo.updateStage(issue1.id, Stage.Done);
      const issue2 = repo.create({ number: 2, projectId, title: 'Done Archived' });
      repo.updateStage(issue2.id, Stage.Done);
      repo.archive(issue2.id);

      const doneIssues = repo.findAll({ projectId, stage: Stage.Done, includeArchived: true });

      expect(doneIssues).toHaveLength(2);
    });

    it('should return empty when all issues are archived and no flags set', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Archived' });
      repo.archive(issue.id);

      const all = repo.findAll({ projectId });

      expect(all).toHaveLength(0);
    });
  });
});

describe('IssueService archive', () => {
  let db: DatabaseManager;
  let issueRepo: IssueRepo;
  let commentRepo: CommentRepo;
  let projectRepo: ProjectRepo;
  let checkpointRepo: PipelineCheckpointRepo;
  let service: IssueService;
  let projectId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    projectId = project.id;

    issueRepo = new IssueRepo(db);
    commentRepo = new CommentRepo(db);
    checkpointRepo = new PipelineCheckpointRepo(db);
    service = new IssueService(issueRepo, commentRepo, projectRepo);
  });

  afterEach(() => {
    db.close();
  });

  describe('archive()', () => {
    it('should archive a done issue with mergeState=merged without warning', async () => {
      const issue = issueRepo.create({ number: 1, projectId, title: 'Done Issue' });
      issueRepo.updateStage(issue.id, Stage.Done);
      issueRepo.setMergeState(issue.id, MergeState.Merged);

      const result = await service.archive(projectId, 1);

      expect(result.issue.archivedAt).toBeDefined();
      expect(result.warning).toBeUndefined();
    });

    it('should warn when archiving non-done issue', async () => {
      const issue = issueRepo.create({ number: 1, projectId, title: 'Active Issue' });
      issueRepo.updateStage(issue.id, Stage.Build);

      const result = await service.archive(projectId, 1);

      expect(result.issue.archivedAt).toBeDefined();
      expect(result.warning).toContain('not completed');
      expect(result.warning).toContain(Stage.Build);
    });

    it('should reject archive when agent is running', async () => {
      const issue = issueRepo.create({ number: 1, projectId, title: 'Running Issue' });
      const runningAgent = { getQueueStatus: vi.fn().mockReturnValue({ running: { id: 't1' }, pending: [], queueLength: 0 }) };
      const svc = new IssueService(issueRepo, commentRepo, projectRepo, undefined, runningAgent);

      await expect(svc.archive(projectId, 1)).rejects.toThrow('Cannot archive');
      await expect(svc.archive(projectId, 1)).rejects.toThrow('running agent');

      const unchanged = issueRepo.findByNumber(projectId, 1);
      expect(unchanged!.archivedAt).toBeUndefined();
    });

    it('should throw for non-existent issue', async () => {
      await expect(service.archive(projectId, 999)).rejects.toThrow('not found');
    });

    it('should skip cleanup when cleanup=false', async () => {
      const issue = issueRepo.create({ number: 1, projectId, title: 'Test' });
      issueRepo.updateStage(issue.id, Stage.Done);

      const mockWorktreeManager = {
        remove: vi.fn().mockResolvedValue(undefined),
      };
      const svc = new IssueService(
        issueRepo, commentRepo, projectRepo,
        mockWorktreeManager as any, undefined, checkpointRepo
      );

      const result = await svc.archive(projectId, 1, { cleanup: false });

      expect(result.issue.archivedAt).toBeDefined();
      expect(mockWorktreeManager.remove).not.toHaveBeenCalled();
    });

    it('should perform cleanup when cleanup=true (default)', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-archive-test-'));
      try {
        const project = projectRepo.create({ name: 'CleanupTest', path: tmpDir });
        const svcProjectId = project.id;

        const issue = issueRepo.create({ number: 1, projectId: svcProjectId, title: 'Test' });
        issueRepo.updateStage(issue.id, Stage.Done);

        const mockWorktreeManager = {
          remove: vi.fn().mockResolvedValue(undefined),
        };
        const svc = new IssueService(
          issueRepo, commentRepo, projectRepo,
          mockWorktreeManager as any, undefined, checkpointRepo
        );

        await svc.archive(svcProjectId, 1, { cleanup: true });

        expect(mockWorktreeManager.remove).toHaveBeenCalledWith(tmpDir, 'CleanupTest', 1);
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });

    it('should cleanup checkpoints during archive', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-archive-ckpt-'));
      try {
        const project = projectRepo.create({ name: 'CkptTest', path: tmpDir });
        const svcProjectId = project.id;

        const issue = issueRepo.create({ number: 1, projectId: svcProjectId, title: 'Test' });
        issueRepo.updateStage(issue.id, Stage.Done);

        checkpointRepo.upsert(1, 'build', ['step1'], 'step2');
        expect(checkpointRepo.get(1, 'build')).not.toBeNull();

        const svc = new IssueService(
          issueRepo, commentRepo, projectRepo,
          undefined, undefined, checkpointRepo
        );

        await svc.archive(svcProjectId, 1);

        expect(checkpointRepo.get(1, 'build')).toBeNull();
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });

    it('should handle missing worktree gracefully during cleanup', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-archive-wt-'));
      try {
        const project = projectRepo.create({ name: 'WtTest', path: tmpDir });
        const svcProjectId = project.id;

        const issue = issueRepo.create({ number: 1, projectId: svcProjectId, title: 'Test' });
        issueRepo.updateStage(issue.id, Stage.Done);

        const mockWorktreeManager = {
          remove: vi.fn().mockRejectedValue(new Error('worktree not found')),
        };
        const svc = new IssueService(
          issueRepo, commentRepo, projectRepo,
          mockWorktreeManager as any, undefined, checkpointRepo
        );

        const result = await svc.archive(svcProjectId, 1);

        expect(result.issue.archivedAt).toBeDefined();
        expect(mockWorktreeManager.remove).toHaveBeenCalled();
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });
  });

  describe('unarchive()', () => {
    it('should clear archived_at on unarchive', async () => {
      const issue = issueRepo.create({ number: 1, projectId, title: 'Test' });
      issueRepo.archive(issue.id);

      const result = await service.unarchive(projectId, 1);

      expect(result.archivedAt).toBeUndefined();
    });

    it('should preserve stage and status after unarchive', async () => {
      const issue = issueRepo.create({ number: 1, projectId, title: 'Test' });
      issueRepo.updateStage(issue.id, Stage.Done);
      issueRepo.updateStatus(issue.id, IssueStatus.Completed);
      issueRepo.archive(issue.id);

      const result = await service.unarchive(projectId, 1);

      expect(result.stage).toBe(Stage.Done);
      expect(result.status).toBe(IssueStatus.Completed);
    });

    it('should throw for non-existent issue', async () => {
      await expect(service.unarchive(projectId, 999)).rejects.toThrow('not found');
    });

    it('should attempt to restore openspec change directory', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-unarchive-test-'));
      try {
        const project = projectRepo.create({ name: 'RestoreTest', path: tmpDir });
        const svcProjectId = project.id;

        const issue = issueRepo.create({ number: 1, projectId: svcProjectId, title: 'Test' });
        issueRepo.archive(issue.id);

        const svc = new IssueService(issueRepo, commentRepo, projectRepo);
        const result = await svc.unarchive(svcProjectId, 1);

        expect(result.archivedAt).toBeUndefined();
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });
  });

  describe('archiveAllCompleted()', () => {
    it('should archive all done issues with mergeState=merged', async () => {
      const issue1 = issueRepo.create({ number: 1, projectId, title: 'Done 1' });
      issueRepo.updateStage(issue1.id, Stage.Done);
      issueRepo.setMergeState(issue1.id, MergeState.Merged);
      const issue2 = issueRepo.create({ number: 2, projectId, title: 'Done 2' });
      issueRepo.updateStage(issue2.id, Stage.Done);
      issueRepo.setMergeState(issue2.id, MergeState.Merged);
      issueRepo.create({ number: 3, projectId, title: 'Not Done' });

      const result = await service.archiveAllCompleted(projectId);

      expect(result.count).toBe(2);
      expect(result.message).toBe('Archived 2 issues.');
    });

    it('should return message when no completed issues', async () => {
      issueRepo.create({ number: 1, projectId, title: 'Active' });

      const result = await service.archiveAllCompleted(projectId);

      expect(result.count).toBe(0);
      expect(result.message).toBe('No completed issues to archive.');
    });

    it('should not archive already-archived done issues', async () => {
      const issue = issueRepo.create({ number: 1, projectId, title: 'Already Archived' });
      issueRepo.updateStage(issue.id, Stage.Done);
      issueRepo.archive(issue.id);

      const result = await service.archiveAllCompleted(projectId);

      expect(result.count).toBe(0);
      expect(result.message).toBe('No completed issues to archive.');
    });

    it('should skip issues with running agents in batch', async () => {
      const issue1 = issueRepo.create({ number: 1, projectId, title: 'Done 1' });
      issueRepo.updateStage(issue1.id, Stage.Done);
      issueRepo.setMergeState(issue1.id, MergeState.Merged);
      const issue2 = issueRepo.create({ number: 2, projectId, title: 'Done 2 Running' });
      issueRepo.updateStage(issue2.id, Stage.Done);
      issueRepo.setMergeState(issue2.id, MergeState.Merged);

      let callCount = 0;
      const runningAgent = {
        getQueueStatus: vi.fn().mockImplementation((id: string) => ({
          running: id === issue2.id ? { id: 't1' } : null,
          pending: [],
          queueLength: 0,
        })),
      };
      const svc = new IssueService(issueRepo, commentRepo, projectRepo, undefined, runningAgent);

      const result = await svc.archiveAllCompleted(projectId);

      expect(result.count).toBe(1);
    });

    it('should skip false-done issues (done/completed + mergeState null) and report skipped', async () => {
      const issue1 = issueRepo.create({ number: 1, projectId, title: 'False Done' });
      issueRepo.updateStage(issue1.id, Stage.Done);
      issueRepo.updateStatus(issue1.id, IssueStatus.Completed);
      // mergeState is null by default (not merged)
      const issue2 = issueRepo.create({ number: 2, projectId, title: 'Really Done' });
      issueRepo.updateStage(issue2.id, Stage.Done);
      issueRepo.setMergeState(issue2.id, MergeState.Merged);

      const result = await service.archiveAllCompleted(projectId);

      expect(result.count).toBe(1);
      expect(result.skipped).toBe(1);
      expect(result.skippedNumbers).toContain(1);
      expect(result.message).toContain('Skipped 1 false-done issue');
    });

    it('should skip false-done issues (done/completed + non-merged mergeState) and report skipped', async () => {
      const issue1 = issueRepo.create({ number: 1, projectId, title: 'False Done Conflict' });
      issueRepo.updateStage(issue1.id, Stage.Done);
      issueRepo.updateStatus(issue1.id, IssueStatus.Completed);
      issueRepo.setMergeState(issue1.id, MergeState.Conflict);
      const issue2 = issueRepo.create({ number: 2, projectId, title: 'Really Done' });
      issueRepo.updateStage(issue2.id, Stage.Done);
      issueRepo.setMergeState(issue2.id, MergeState.Merged);

      const result = await service.archiveAllCompleted(projectId);

      expect(result.count).toBe(1);
      expect(result.skipped).toBe(1);
      expect(result.skippedNumbers).toContain(1);
      expect(result.message).toContain('Skipped 1 false-done issue');
    });
  });
});

describe('IssueService archive — false-done guard', () => {
  let db: DatabaseManager;
  let issueRepo: IssueRepo;
  let commentRepo: CommentRepo;
  let projectRepo: ProjectRepo;
  let service: IssueService;
  let projectId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    projectId = project.id;

    issueRepo = new IssueRepo(db);
    commentRepo = new CommentRepo(db);
    service = new IssueService(issueRepo, commentRepo, projectRepo);
  });

  afterEach(() => {
    db.close();
  });

  it('should return falseDoneWarning when archiving done/completed + mergeState null issue', async () => {
    const issue = issueRepo.create({ number: 1, projectId, title: 'False Done' });
    issueRepo.updateStage(issue.id, Stage.Done);
    issueRepo.updateStatus(issue.id, IssueStatus.Completed);
    // mergeState is null by default

    const result = await service.archive(projectId, 1);

    expect(result.issue.archivedAt).toBeDefined();
    expect(result.falseDoneWarning).toBe(true);
    expect(result.warning).toContain('not been merged');
    expect(result.warning).toContain('mergeState: null');
  });

  it('should return falseDoneWarning when archiving done/completed + mergeState conflict issue', async () => {
    const issue = issueRepo.create({ number: 1, projectId, title: 'False Done Conflict' });
    issueRepo.updateStage(issue.id, Stage.Done);
    issueRepo.updateStatus(issue.id, IssueStatus.Completed);
    issueRepo.setMergeState(issue.id, MergeState.Conflict);

    const result = await service.archive(projectId, 1);

    expect(result.issue.archivedAt).toBeDefined();
    expect(result.falseDoneWarning).toBe(true);
    expect(result.warning).toContain('not been merged');
    expect(result.warning).toContain('mergeState: conflict');
  });

  it('should NOT return falseDoneWarning when archiving truly merged issue', async () => {
    const issue = issueRepo.create({ number: 1, projectId, title: 'Truly Merged' });
    issueRepo.updateStage(issue.id, Stage.Done);
    issueRepo.updateStatus(issue.id, IssueStatus.Completed);
    issueRepo.setMergeState(issue.id, MergeState.Merged);

    const result = await service.archive(projectId, 1);

    expect(result.issue.archivedAt).toBeDefined();
    expect(result.falseDoneWarning).toBe(false);
    expect(result.warning).toBeUndefined();
  });

  it('should still archive false-done issue but with warning (not blocking)', async () => {
    const issue = issueRepo.create({ number: 1, projectId, title: 'False Done' });
    issueRepo.updateStage(issue.id, Stage.Done);
    issueRepo.updateStatus(issue.id, IssueStatus.Completed);

    // Should not throw, should archive with warning
    const result = await service.archive(projectId, 1);

    expect(result.issue.archivedAt).toBeDefined();
    expect(result.falseDoneWarning).toBe(true);
  });
});

describe('Archive API Endpoints', () => {
  let db: DatabaseManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let issueRepo: IssueRepo;
  let stateManager: StateManager;
  let server: http.Server;
  let projectId: string;

  beforeEach(async () => {
    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);

    const projectRepo = stateManager.getProjectRepo();
    issueRepo = stateManager.getIssueRepo();
    const configRepo = stateManager.getConfigRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();

    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);

    const eventBus = new EventBus();
    const agentRunner = new AgentRunnerService(eventBus);

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService, projectService, stateManager,
      undefined, undefined, undefined, agentRunner
    ));
    server = createTestServer(app);

    const project = await projectService.create({ name: 'ArchiveTest', path: '/test' });
    projectId = project.id;
    projectService.setCurrent(project);
  });

  afterEach(() => {
    server.close();
    db.close();
  });

  describe('GET /api/issues', () => {
    it('should exclude archived issues by default', async () => {
      await issueService.create({ projectId, title: 'Active' });
      const issue2 = await issueService.create({ projectId, title: 'Archived' });
      issueRepo.updateStage(issue2.id, Stage.Done);
      await issueService.archive(projectId, 2);

      const response = await request(server).get('/api/issues');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(1);
      expect(response.body.data[0].number).toBe(1);
    });

    it('should show only archived issues with ?archived=true', async () => {
      await issueService.create({ projectId, title: 'Active' });
      const issue2 = await issueService.create({ projectId, title: 'Archived' });
      issueRepo.updateStage(issue2.id, Stage.Done);
      await issueService.archive(projectId, 2);

      const response = await request(server).get('/api/issues?archived=true');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(1);
      expect(response.body.data[0].number).toBe(2);
    });

    it('should show all issues with ?all=true', async () => {
      await issueService.create({ projectId, title: 'Active' });
      const issue2 = await issueService.create({ projectId, title: 'Archived' });
      issueRepo.updateStage(issue2.id, Stage.Done);
      await issueService.archive(projectId, 2);

      const response = await request(server).get('/api/issues?all=true');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(2);
    });
  });

  describe('POST /api/issues/:number/archive', () => {
    it('should archive an issue', async () => {
      await issueService.create({ projectId, title: 'To Archive' });
      issueRepo.updateStage(
        issueRepo.findByNumber(projectId, 1)!.id,
        Stage.Done
      );

      const response = await request(server)
        .post('/api/issues/1/archive')
        .send({ cleanup: false });

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.issue.archivedAt).toBeDefined();
      expect(response.body.data.message).toContain('archived');
    });

    it('should return warning for non-done issue', async () => {
      await issueService.create({ projectId, title: 'Not Done' });

      const response = await request(server)
        .post('/api/issues/1/archive')
        .send({ cleanup: false });

      expect(response.status).toBe(200);
      expect(response.body.data.warning).toContain('not completed');
    });

    it('should return 404 for non-existent issue', async () => {
      const response = await request(server)
        .post('/api/issues/999/archive')
        .send({});

      expect(response.status).toBe(404);
      expect(response.body.error).toContain('not found');
    });

    it('should return 400 when no active project', async () => {
      projectService.clearCurrent();

      const response = await request(server)
        .post('/api/issues/1/archive')
        .send({});

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('No active project');
    });
  });

  describe('POST /api/issues/:number/unarchive', () => {
    it('should unarchive an archived issue', async () => {
      await issueService.create({ projectId, title: 'Test' });
      issueRepo.updateStage(
        issueRepo.findByNumber(projectId, 1)!.id,
        Stage.Done
      );
      await issueService.archive(projectId, 1);

      const response = await request(server)
        .post('/api/issues/1/unarchive')
        .send({});

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.issue.archivedAt).toBeUndefined();
      expect(response.body.data.message).toContain('unarchived');
    });

    it('should return 400 for non-archived issue', async () => {
      await issueService.create({ projectId, title: 'Active' });

      const response = await request(server)
        .post('/api/issues/1/unarchive')
        .send({});

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('not archived');
    });

    it('should return 404 for non-existent issue', async () => {
      const response = await request(server)
        .post('/api/issues/999/unarchive')
        .send({});

      expect(response.status).toBe(404);
    });
  });

  describe('POST /api/issues/archive-completed', () => {
    it('should archive all completed issues with mergeState=merged', async () => {
      const issue1 = await issueService.create({ projectId, title: 'Done 1' });
      issueRepo.updateStage(issue1.id, Stage.Done);
      issueRepo.setMergeState(issue1.id, MergeState.Merged);
      const issue2 = await issueService.create({ projectId, title: 'Done 2' });
      issueRepo.updateStage(issue2.id, Stage.Done);
      issueRepo.setMergeState(issue2.id, MergeState.Merged);
      await issueService.create({ projectId, title: 'Not Done' });

      const response = await request(server)
        .post('/api/issues/archive-completed')
        .send({});

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.archived).toBe(2);
      expect(response.body.data.message).toContain('2 issues');
    });

    it('should return zero when no completed issues', async () => {
      await issueService.create({ projectId, title: 'Active' });

      const response = await request(server)
        .post('/api/issues/archive-completed')
        .send({});

      expect(response.status).toBe(200);
      expect(response.body.data.archived).toBe(0);
      expect(response.body.data.message).toBe('No completed issues to archive.');
    });

    it('should return 400 when no active project', async () => {
      projectService.clearCurrent();

      const response = await request(server)
        .post('/api/issues/archive-completed')
        .send({});

      expect(response.status).toBe(400);
    });
  });
});

describe('Archive CLI Commands', () => {
  it('should have archive subcommand', () => {
    const program = new Command();
    setupIssueCommands(program);

    const issueCmd = program.commands.find(cmd => cmd.name() === 'issue');
    expect(issueCmd?.commands.some(cmd => cmd.name() === 'archive')).toBe(true);
  });

  it('should have unarchive subcommand', () => {
    const program = new Command();
    setupIssueCommands(program);

    const issueCmd = program.commands.find(cmd => cmd.name() === 'issue');
    expect(issueCmd?.commands.some(cmd => cmd.name() === 'unarchive')).toBe(true);
  });

  it('archive command should support --all-completed flag', () => {
    const program = new Command();
    setupIssueCommands(program);

    const issueCmd = program.commands.find(cmd => cmd.name() === 'issue');
    const archiveCmd = issueCmd?.commands.find(cmd => cmd.name() === 'archive');

    expect(archiveCmd?.options.some(opt => opt.long === '--all-completed')).toBe(true);
  });

  it('archive command should support --no-cleanup flag', () => {
    const program = new Command();
    setupIssueCommands(program);

    const issueCmd = program.commands.find(cmd => cmd.name() === 'issue');
    const archiveCmd = issueCmd?.commands.find(cmd => cmd.name() === 'archive');

    expect(archiveCmd?.options.some(opt => opt.long === '--no-cleanup')).toBe(true);
  });

  it('list command should support --archived flag', () => {
    const program = new Command();
    setupIssueCommands(program);

    const issueCmd = program.commands.find(cmd => cmd.name() === 'issue');
    const listCmd = issueCmd?.commands.find(cmd => cmd.name() === 'list');

    expect(listCmd?.options.some(opt => opt.long === '--archived')).toBe(true);
  });

  it('list command should support --all flag', () => {
    const program = new Command();
    setupIssueCommands(program);

    const issueCmd = program.commands.find(cmd => cmd.name() === 'issue');
    const listCmd = issueCmd?.commands.find(cmd => cmd.name() === 'list');

    expect(listCmd?.options.some(opt => opt.long === '--all')).toBe(true);
  });
});
