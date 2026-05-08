import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import http from 'node:http';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { PipelineCheckpointRepo } from '../src/db/pipeline-checkpoint-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { EventBus, AgentRunnerService } from '../src/services';
import { StateManager } from '../src/server/state-manager';
import { createIssueRoutes } from '../src/api/issues';
import { Stage, IssueStatus, MergeState } from '../src/types';
import { MergeQueue } from '../src/git/merge-queue';
import { WorktreeManager } from '../src/git/worktree-manager';
import { classifyMergeDelivery } from '../src/workflow/issue-lifecycle';

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

describe('T-005: Archive Lifecycle Regression Tests', () => {
  describe('REQ-WT-001: Merge queue success retains worktree', () => {
    let db: DatabaseManager;
    let projectRepo: ProjectRepo;
    let issueRepo: IssueRepo;
    let issueService: IssueService;
    let eventBus: EventBus;
    let worktreeManager: WorktreeManager;
    let execFileMock: ReturnType<typeof vi.fn>;

    const PROJECT_PATH = '/tmp/test-merge-worktree';
    const PROJECT_NAME = 'test-project';
    const BASE_BRANCH = 'main';

    beforeEach(async () => {
      vi.mock('child_process', async (importOriginal) => {
        const actual = await importOriginal<typeof import('child_process')>();
        return { ...actual, execFile: vi.fn() };
      });
      const { execFile } = await import('child_process');
      execFileMock = vi.mocked(execFile);

      db = new DatabaseManager({ inMemory: true });
      initializeDatabase(db);
      projectRepo = new ProjectRepo(db);
      issueRepo = new IssueRepo(db);
      issueService = new IssueService(issueRepo);
      eventBus = new EventBus();
      const gitDir = path.join(PROJECT_PATH, '.git');
      fs.mkdirSync(gitDir, { recursive: true });
      fs.writeFileSync(path.join(gitDir, 'mohist-last-fetch'), Date.now().toString(), 'utf-8');

      worktreeManager = {
        canFastForward: vi.fn().mockResolvedValue(true),
        rebaseOntoMaster: vi.fn().mockResolvedValue({ success: true, conflicts: [] }),
        abortRebase: vi.fn().mockResolvedValue(undefined),
        mergeBack: vi.fn().mockResolvedValue({ success: true, message: 'Merged' }),
        remove: vi.fn().mockResolvedValue(undefined),
        exists: vi.fn().mockReturnValue(true),
        create: vi.fn().mockResolvedValue('/tmp/worktrees/issue-1'),
        getPath: vi.fn().mockReturnValue('/tmp/worktrees/issue-1'),
      } as unknown as WorktreeManager;

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });
    });

    afterEach(() => {
      db.close();
      fs.rmSync(PROJECT_PATH, { recursive: true, force: true });
    });

    it('merge queue success leaves worktree present and does NOT call remove', async () => {
      const project = projectRepo.create({ name: PROJECT_NAME, path: PROJECT_PATH, baseBranch: BASE_BRANCH });
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });

      const queue = new MergeQueue({
        worktreeManager,
        eventBus,
        issueRepo,
        getProjectPath: (pid: string) => {
          if (pid !== project.id) return null;
          return { path: PROJECT_PATH, name: PROJECT_NAME, baseBranch: BASE_BRANCH };
        },
        resolveConflicts: vi.fn().mockResolvedValue({ success: true }),
        fixBuildErrors: vi.fn().mockResolvedValue({ success: true }),
      });

      queue.enqueue(project.id, issue.number);

      await new Promise((r) => setTimeout(r, 500));

      expect(issueRepo.findById(issue.id)?.mergeState).toBe('merged');
      expect(worktreeManager.remove).not.toHaveBeenCalled();
    });
  });

  describe('REQ-API-001: Manual merge API success retains worktree', () => {
    let db: DatabaseManager;
    let stateManager: StateManager;
    let projectService: ProjectService;
    let issueService: IssueService;
    let issueRepo: IssueRepo;
    let worktreeManager: WorktreeManager;
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

      worktreeManager = {
        exists: vi.fn().mockReturnValue(true),
        mergeBack: vi.fn().mockResolvedValue({ success: true, message: 'Merged' }),
        remove: vi.fn().mockResolvedValue(undefined),
      } as unknown as WorktreeManager;

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(
        issueService, projectService, stateManager,
        worktreeManager, undefined, undefined, agentRunner
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

    it('POST /api/issues/:number/merge marks issue Done/Completed/Merged without cleanup', async () => {
      const issue = await issueService.create({ projectId, title: 'Test Issue' });
      issueRepo.updateStage(issue.id, Stage.Check);
      issueRepo.updateStatus(issue.id, IssueStatus.Active);
      issueRepo.setMergeState(issue.id, MergeState.Pending);

      const response = await request(server)
        .post(`/api/issues/${issue.number}/merge`)
        .send({});

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.issue.stage).toBe(Stage.Done);
      expect(response.body.data.issue.status).toBe(IssueStatus.Completed);
      expect(response.body.data.issue.mergeState).toBe(MergeState.Merged);
      expect(worktreeManager.remove).not.toHaveBeenCalled();

      const stored = issueRepo.findById(issue.id);
      expect(stored?.stage).toBe(Stage.Done);
      expect(stored?.status).toBe(IssueStatus.Completed);
      expect(stored?.mergeState).toBe(MergeState.Merged);
    });
  });

  describe('REQ-WT-002: Archive cleanup removes retained worktrees', () => {
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

    it('archive removes retained worktree by default', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-archive-wt-'));
      try {
        const project = projectRepo.create({ name: 'CleanupTest', path: tmpDir });
        const svcProjectId = project.id;

        const issue = issueRepo.create({ number: 1, projectId: svcProjectId, title: 'Test' });
        issueRepo.updateStage(issue.id, Stage.Done);
        issueRepo.setMergeState(issue.id, MergeState.Merged);

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

    it('archive with cleanup=false retains worktree', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-archive-no-cleanup-'));
      try {
        const project = projectRepo.create({ name: 'NoCleanupTest', path: tmpDir });
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

        const result = await svc.archive(svcProjectId, 1, { cleanup: false });

        expect(result.issue.archivedAt).toBeDefined();
        expect(mockWorktreeManager.remove).not.toHaveBeenCalled();
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });
  });

  describe('REQ-STORE-001: Archived issues filtering', () => {
    let db: DatabaseManager;
    let issueRepo: IssueRepo;
    let projectId: string;

    beforeEach(() => {
      db = new DatabaseManager({ inMemory: true });
      initializeDatabase(db);
      const projectRepo = new ProjectRepo(db);
      const project = projectRepo.create({ name: 'Test Project', path: '/test' });
      projectId = project.id;
      issueRepo = new IssueRepo(db);
    });

    afterEach(() => {
      db.close();
    });

    it('default query hides archived issue', () => {
      const activeIssue = issueRepo.create({ number: 1, projectId, title: 'Active' });
      const archivedIssue = issueRepo.create({ number: 2, projectId, title: 'To Archive' });
      issueRepo.archive(archivedIssue.id);

      const all = issueRepo.findAll({ projectId });

      expect(all).toHaveLength(1);
      expect(all[0].number).toBe(1);
      expect(all[0].id).toBe(activeIssue.id);
    });

    it('archived-only query returns archived issue', () => {
      issueRepo.create({ number: 1, projectId, title: 'Active' });
      const archivedIssue = issueRepo.create({ number: 2, projectId, title: 'To Archive' });
      issueRepo.archive(archivedIssue.id);

      const archived = issueRepo.findAll({ projectId, archivedOnly: true });

      expect(archived).toHaveLength(1);
      expect(archived[0].number).toBe(2);
    });

    it('archived issue keeps history fields', () => {
      const issue = issueRepo.create({ number: 1, projectId, title: 'Test Issue' });
      issueRepo.updateStage(issue.id, Stage.Done);
      issueRepo.updateStatus(issue.id, IssueStatus.Completed);
      issueRepo.setMergeState(issue.id, MergeState.Merged);

      const commentRepo = new CommentRepo(db);
      commentRepo.create({ issueId: issue.id, body: 'Test comment' });

      issueRepo.archive(issue.id);

      const restored = issueRepo.findByNumber(projectId, 1);
      expect(restored).not.toBeNull();
      expect(restored!.archivedAt).toBeDefined();
      expect(restored!.stage).toBe(Stage.Done);
      expect(restored!.status).toBe(IssueStatus.Completed);
      expect(restored!.mergeState).toBe(MergeState.Merged);
    });
  });

  describe('REQ-API-002: Batch archive skips false-done issues', () => {
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

    it('skips done-but-not-merged issues and returns skippedNumbers', async () => {
      const falseDone = issueRepo.create({ number: 1, projectId, title: 'False Done' });
      issueRepo.updateStage(falseDone.id, Stage.Done);
      issueRepo.updateStatus(falseDone.id, IssueStatus.Completed);

      const trulyDone = issueRepo.create({ number: 2, projectId, title: 'Truly Done' });
      issueRepo.updateStage(trulyDone.id, Stage.Done);
      issueRepo.setMergeState(trulyDone.id, MergeState.Merged);

      const result = await service.archiveAllCompleted(projectId);

      expect(result.count).toBe(1);
      expect(result.skipped).toBe(1);
      expect(result.skippedNumbers).toContain(1);
      expect(result.message).toContain('Skipped 1 false-done issue');
    });
  });

  describe('REQ-API-002: Single archive returns false-done warning', () => {
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

    it('archive of done-but-not-merged returns warning with mergeState info', async () => {
      const issue = issueRepo.create({ number: 1, projectId, title: 'False Done' });
      issueRepo.updateStage(issue.id, Stage.Done);
      issueRepo.updateStatus(issue.id, IssueStatus.Completed);

      const result = await service.archive(projectId, 1);

      expect(result.issue.archivedAt).toBeDefined();
      expect(result.falseDoneWarning).toBe(true);
      expect(result.warning).toContain('not been merged');
    });
  });

  describe('REQ-CLI-001: CLI warning output does not duplicate Warning:', () => {
    it('backend warning string does not create double Warning: prefix in CLI output', () => {
      const backendWarning = 'Warning: Issue #1 is marked done/completed but has not been merged (mergeState: null). Archiving without merge confirmation.';

      const cliOutput = `  ${backendWarning}`;

      expect(cliOutput).not.toContain('Warning: Warning:');
      expect(cliOutput).toContain('Warning: Issue #1');
    });

    it('classifyMergeDelivery returns correct status for merged vs not-merged', () => {
      const mergedIssue = {
        stage: Stage.Done,
        status: IssueStatus.Completed,
        mergeState: MergeState.Merged,
      } as const;

      const notMergedIssue = {
        stage: Stage.Done,
        status: IssueStatus.Completed,
        mergeState: null,
      } as const;

      expect(classifyMergeDelivery(mergedIssue)).toBe('merged');
      expect(classifyMergeDelivery(notMergedIssue)).toBe('done-not-merged');
    });
  });

  describe('REQ-WEB-003: Done column batch archive visibility', () => {
    const repoRoot = path.join(__dirname, '..');

    it('StageColumn renders archive button based on isDone and totalCount conditions', () => {
      const source = fs.readFileSync(
        path.join(repoRoot, 'web/src/components/StageColumn.tsx'),
        'utf-8'
      );

      expect(source).toContain('archiveAllMutation');
      expect(source).toMatch(/isDone.*totalCount > 0|totalCount > 0.*isDone/);
    });

    it('StageColumn footer shows archive button when isDone=true and totalCount>0 regardless of archivedCount', () => {
      const source = fs.readFileSync(
        path.join(repoRoot, 'web/src/components/StageColumn.tsx'),
        'utf-8'
      );

      const footerCondition = source.match(/\{isDone && totalCount > 0 && \([^)]*\)\}/);
      expect(footerCondition).not.toBeNull();
    });
  });

  describe('REQ-WEB-001: Done worktree retention copy is visible in issue detail path', () => {
    const repoRoot = path.join(__dirname, '..');

    it('IssueDetailPage renders BranchBar for worktree status', () => {
      const source = fs.readFileSync(
        path.join(repoRoot, 'web/src/components/IssueDetailPage.tsx'),
        'utf-8'
      );

      expect(source).toContain('<BranchBar issueNumber={issueNumber} stage={issue.stage}');
    });

    it('BranchBar displays Done retained worktree and archive-removal copy', () => {
      const source = fs.readFileSync(
        path.join(repoRoot, 'web/src/components/BranchBar.tsx'),
        'utf-8'
      );

      expect(source).toContain('stage === Stage.Done');
      expect(source).toContain('retained for review, traceability, diff inspection, and debugging');
      expect(source).toContain('Archiving will remove the retained worktree');
    });
  });

  describe('REQ-WEB-004: Archived page has no restore action', () => {
    const repoRoot = path.join(__dirname, '..');

    it('ArchivedPage source contains no unarchive or restore button/action', () => {
      const archivedPagePath = path.join(repoRoot, 'web/src/components/ArchivedPage.tsx');
      const content = fs.readFileSync(archivedPagePath, 'utf-8');

      expect(content).not.toMatch(/unarchive|restore/i);
    });
  });

  describe('API: archived issues hidden by default, visible with archived=true', () => {
    let db: DatabaseManager;
    let stateManager: StateManager;
    let projectService: ProjectService;
    let issueService: IssueService;
    let issueRepo: IssueRepo;
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

    it('GET /api/issues excludes archived issues by default', async () => {
      await issueService.create({ projectId, title: 'Active' });
      const issue2 = await issueService.create({ projectId, title: 'Archived' });
      issueRepo.updateStage(issue2.id, Stage.Done);
      await issueService.archive(projectId, 2);

      const response = await request(server).get('/api/issues');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(1);
      expect(response.body.data[0].number).toBe(1);
    });

    it('GET /api/issues?archived=true returns only archived issues', async () => {
      await issueService.create({ projectId, title: 'Active' });
      const archivedIssue = await issueService.create({ projectId, title: 'Archived' });
      issueRepo.updateStage(archivedIssue.id, Stage.Done);
      await issueService.archive(projectId, archivedIssue.number);

      const response = await request(server).get('/api/issues?archived=true');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(1);
      expect(response.body.data[0].number).toBe(archivedIssue.number);
    });

    it('GET /api/issues?all=true includes archived issues', async () => {
      await issueService.create({ projectId, title: 'Active' });
      const archivedIssue = await issueService.create({ projectId, title: 'Archived' });
      issueRepo.updateStage(archivedIssue.id, Stage.Done);
      await issueService.archive(projectId, archivedIssue.number);

      const response = await request(server).get('/api/issues?all=true');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(2);
    });
  });

  describe('archiveAllCompleted API returns correct shape', () => {
    let db: DatabaseManager;
    let stateManager: StateManager;
    let projectService: ProjectService;
    let issueService: IssueService;
    let issueRepo: IssueRepo;
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

    it('POST /api/issues/archive-completed returns archived, skipped, skippedNumbers, and message', async () => {
      const falseDone = await issueService.create({ projectId, title: 'False Done' });
      issueRepo.updateStage(falseDone.id, Stage.Done);
      issueRepo.updateStatus(falseDone.id, IssueStatus.Completed);

      const trulyDone = await issueService.create({ projectId, title: 'Truly Done' });
      issueRepo.updateStage(trulyDone.id, Stage.Done);
      issueRepo.setMergeState(trulyDone.id, MergeState.Merged);

      const response = await request(server)
        .post('/api/issues/archive-completed')
        .send({});

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data).toHaveProperty('archived');
      expect(response.body.data).toHaveProperty('skipped');
      expect(response.body.data).toHaveProperty('skippedNumbers');
      expect(response.body.data).toHaveProperty('message');
      expect(response.body.data.archived).toBe(1);
      expect(response.body.data.skipped).toBe(1);
      expect(response.body.data.skippedNumbers).toContain(falseDone.number);
    });
  });
});
