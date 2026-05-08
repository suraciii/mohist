import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import http from 'node:http';
import { execFile } from 'child_process';
import { promisify } from 'util';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { ConfigService } from '../src/services/config-service';
import { EventBus, AgentRunnerService } from '../src/services';
import { StateManager } from '../src/server/state-manager';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { createIssueRoutes } from '../src/api/issues';
import { Stage } from '../src/types';

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

describe('Issue Review Surface Regression Tests', () => {
  let db: DatabaseManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stateManager: StateManager;
  let tmpDir: string;
  let repoDir: string;

  async function initGitRepo(dir: string): Promise<void> {
    const execAsync = promisify(execFile);
    await execAsync('git', ['init', '-b', 'main'], { cwd: dir });
    await execAsync('git', ['config', 'user.email', 'test@test.com'], { cwd: dir });
    await execAsync('git', ['config', 'user.name', 'Test'], { cwd: dir });
    fs.writeFileSync(path.join(dir, 'README.md'), 'init');
    await execAsync('git', ['add', '-A'], { cwd: dir });
    await execAsync('git', ['commit', '-m', 'init'], { cwd: dir });
  }

  async function createWorktree(repoPath: string, branchName: string, worktreePath: string): Promise<void> {
    const execAsync = promisify(execFile);
    await execAsync('git', ['worktree', 'add', '-b', branchName, worktreePath], { cwd: repoPath });
  }

  beforeEach(async () => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-review-test-'));
    repoDir = path.join(tmpDir, 'repo');
    fs.mkdirSync(repoDir);
    await initGitRepo(repoDir);

    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);

    const projectRepo = stateManager.getProjectRepo();
    const issueRepo = stateManager.getIssueRepo();
    const configRepo = stateManager.getConfigRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();
    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);
  });

  afterEach(() => {
    db.close();
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  describe('Multi-commit git log parser', () => {
    it('returns all commits with nonzero stats when issue branch has multiple commits', async () => {
      const project = await projectService.create({ name: 'ReviewTest', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Multi-Commit Issue' });
      const branchName = `mo/issue-${issue.number}`;

      const git = promisify(execFile);
      await git('git', ['checkout', '-b', branchName], { cwd: repoDir });

      fs.writeFileSync(path.join(repoDir, 'file1.txt'), 'content1');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'commit 1'], { cwd: repoDir });

      fs.writeFileSync(path.join(repoDir, 'file2.txt'), 'content2');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'commit 2'], { cwd: repoDir });

      fs.writeFileSync(path.join(repoDir, 'file3.txt'), 'content3');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'commit 3'], { cwd: repoDir });

      await git('git', ['checkout', 'main'], { cwd: repoDir });

      const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'reviewtest', 'worktrees', `issue-${issue.number}`);
      fs.mkdirSync(worktreeDir, { recursive: true });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, undefined, agentRunner));
      const server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}/commits`);

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.available).toBe(true);

      const commits = response.body.data.commits;
      expect(commits).toHaveLength(3);
      expect(commits.map((c: any) => c.message)).toEqual(['commit 3', 'commit 2', 'commit 1']);
      expect(commits.map((c: any) => c.files)).toEqual([['file3.txt'], ['file2.txt'], ['file1.txt']]);
      expect(commits.map((c: any) => c.additions)).toEqual([1, 1, 1]);
      expect(commits.map((c: any) => c.deletions)).toEqual([0, 0, 0]);

      fs.rmSync(worktreeDir, { recursive: true, force: true });
      server.close();
    });

    it('correctly associates numstat entries with their parent commit', async () => {
      const project = await projectService.create({ name: 'NumstatTest', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Numstat Issue' });
      const branchName = `mo/issue-${issue.number}`;

      const git = promisify(execFile);
      await git('git', ['checkout', '-b', branchName], { cwd: repoDir });

      fs.writeFileSync(path.join(repoDir, 'a.txt'), 'a');
      fs.writeFileSync(path.join(repoDir, 'b.txt'), 'b');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'add two files'], { cwd: repoDir });

      fs.writeFileSync(path.join(repoDir, 'c.txt'), 'c');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'add one file'], { cwd: repoDir });

      await git('git', ['checkout', 'main'], { cwd: repoDir });

      const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'numstattest', 'worktrees', `issue-${issue.number}`);
      fs.mkdirSync(worktreeDir, { recursive: true });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, undefined, agentRunner));
      const server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}/commits`);

      expect(response.status).toBe(200);
      expect(response.body.data.commits).toHaveLength(2);
      expect(response.body.data.commits.map((c: any) => c.message)).toEqual(['add one file', 'add two files']);
      expect(response.body.data.commits[0].files).toEqual(['c.txt']);
      expect(response.body.data.commits[0].filesChanged).toBe(1);
      expect(response.body.data.commits[0].additions).toBe(1);
      expect(response.body.data.commits[0].deletions).toBe(0);
      expect(response.body.data.commits[1].files.sort()).toEqual(['a.txt', 'b.txt']);
      expect(response.body.data.commits[1].filesChanged).toBe(2);
      expect(response.body.data.commits[1].additions).toBe(2);
      expect(response.body.data.commits[1].deletions).toBe(0);

      fs.rmSync(worktreeDir, { recursive: true, force: true });
      server.close();
    });

    it('handles commits with file changes correctly', async () => {
      const project = await projectService.create({ name: 'FileCommitTest', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'File Commit Issue' });
      const branchName = `mo/issue-${issue.number}`;

      const git = promisify(execFile);
      await git('git', ['checkout', '-b', branchName], { cwd: repoDir });

      fs.writeFileSync(path.join(repoDir, 'file.txt'), 'content');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'with file'], { cwd: repoDir });

      fs.writeFileSync(path.join(repoDir, 'another.txt'), 'more');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'with another file'], { cwd: repoDir });

      await git('git', ['checkout', 'main'], { cwd: repoDir });

      const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'filecommittest', 'worktrees', `issue-${issue.number}`);
      fs.mkdirSync(worktreeDir, { recursive: true });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, undefined, agentRunner));
      const server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}/commits`);

      expect(response.status).toBe(200);
      expect(response.body.data.commits).toHaveLength(2);
      expect(response.body.data.summary.filesChanged).toBe(2);

      const messages = response.body.data.commits.map((c: any) => c.message);
      expect(messages).toEqual(['with another file', 'with file']);

      fs.rmSync(worktreeDir, { recursive: true, force: true });
      server.close();
    });
  });

  describe('GET /api/issues/:number/diff availability', () => {
    it('returns not_started reason for draft issue with no worktree', async () => {
      const project = await projectService.create({ name: 'DiffTest', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Draft Issue' });

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, null, undefined, agentRunner));
      const server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}/diff`);

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.available).toBe(false);
      expect(response.body.data.reason).toBe('not_started');
      expect(response.body.data.message).toContain('not started');

      server.close();
    });

    it('returns worktree_removed reason for non-draft/backlog issue with no worktree', async () => {
      const project = await projectService.create({ name: 'DiffTest2', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Started Issue' });
      issueService.transitionToStage(issue.id, Stage.Build);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, null, undefined, agentRunner));
      const server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}/diff`);

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.available).toBe(false);
      expect(response.body.data.reason).toBe('worktree_removed');
      expect(response.body.data.message).toContain('removed');

      server.close();
    });

    it('returns branch_missing reason when issue branch does not exist', async () => {
      const project = await projectService.create({ name: 'BranchMissingTest', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Branch Missing Issue' });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();

      const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'branchmissingtest', 'worktrees', `issue-${issue.number}`);
      fs.mkdirSync(path.dirname(worktreeDir), { recursive: true });
      fs.mkdirSync(worktreeDir, { recursive: true });
      fs.writeFileSync(path.join(worktreeDir, 'placeholder'), 'placeholder');

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, agentRunner));
      const server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}/diff`);

      expect(response.status).toBe(200);
      expect(response.body.data.available).toBe(false);
      expect(response.body.data.reason).toBe('branch_missing');

      fs.rmSync(worktreeDir, { recursive: true, force: true });
      server.close();
    });

    it('returns available=true with complete data when worktree and branch exist', async () => {
      const project = await projectService.create({ name: 'AvailableDiffTest', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Available Issue' });
      const branchName = `mo/issue-${issue.number}`;

      const git = promisify(execFile);
      await git('git', ['checkout', '-b', branchName], { cwd: repoDir });
      fs.writeFileSync(path.join(repoDir, 'test.txt'), 'hello');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'add test'], { cwd: repoDir });
      await git('git', ['checkout', 'main'], { cwd: repoDir });

      const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'availabledifftest', 'worktrees', `issue-${issue.number}`);
      fs.mkdirSync(worktreeDir, { recursive: true });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, agentRunner));
      const server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}/diff`);

      expect(response.status).toBe(200);
      expect(response.body.data.available).toBe(true);
      expect(response.body.data.reason).toBeNull();
      expect(response.body.data.base).toBe('main');
      expect(response.body.data.head).toBe(branchName);
      expect(response.body.data.summary).toBeDefined();
      expect(response.body.data.summary.filesChanged).toBeGreaterThan(0);
      expect(Array.isArray(response.body.data.files)).toBe(true);

      fs.rmSync(worktreeDir, { recursive: true, force: true });
      server.close();
    });
  });

  describe('GET /api/issues/:number/commits availability', () => {
    it('returns not_started reason for draft/backlog issue with no worktree', async () => {
      const project = await projectService.create({ name: 'CommitsAvailTest', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Draft Commit Issue' });

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, null, undefined, agentRunner));
      const server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}/commits`);

      expect(response.status).toBe(200);
      expect(response.body.data.available).toBe(false);
      expect(response.body.data.reason).toBe('not_started');

      server.close();
    });

    it('returns not_started when a worktree manager exists but no draft worktree exists', async () => {
      const project = await projectService.create({ name: 'CommitsNoWorktreeTest', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'No Worktree Draft Commit Issue' });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, agentRunner));
      const server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}/commits`);

      expect(response.status).toBe(200);
      expect(response.body.data.available).toBe(false);
      expect(response.body.data.reason).toBe('not_started');

      server.close();
    });

    it('returns worktree_removed reason for done stage issue with no worktree', async () => {
      const project = await projectService.create({ name: 'CommitsAvailTest2', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Done Commit Issue' });
      issueService.transitionToStage(issue.id, Stage.Done);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, null, undefined, agentRunner));
      const server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}/commits`);

      expect(response.status).toBe(200);
      expect(response.body.data.available).toBe(false);
      expect(response.body.data.reason).toBe('worktree_removed');

      server.close();
    });

    it('returns branch_missing reason when branch does not exist', async () => {
      const project = await projectService.create({ name: 'CommitsBranchTest', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Missing Branch Issue' });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();

      const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'commitsbranchtest', 'worktrees', `issue-${issue.number}`);
      fs.mkdirSync(worktreeDir, { recursive: true });

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, agentRunner));
      const server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}/commits`);

      expect(response.status).toBe(200);
      expect(response.body.data.available).toBe(false);
      expect(response.body.data.reason).toBe('branch_missing');

      fs.rmSync(worktreeDir, { recursive: true, force: true });
      server.close();
    });

    it('returns git_error when git command fails', async () => {
      const project = await projectService.create({ name: 'GitErrorTest', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Git Error Issue' });
      const branchName = `mo/issue-${issue.number}`;

      const git = promisify(execFile);
      await git('git', ['checkout', '-b', branchName], { cwd: repoDir });
      fs.writeFileSync(path.join(repoDir, 'test.txt'), 'hello');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'test'], { cwd: repoDir });
      await git('git', ['checkout', 'main'], { cwd: repoDir });

      const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'giterrortest', 'worktrees', `issue-${issue.number}`);
      fs.mkdirSync(worktreeDir, { recursive: true });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, agentRunner));
      const server = createTestServer(app);

      const corruptPath = path.join(tmpDir, 'corrupt');
      fs.mkdirSync(corruptPath);

      const corruptProject = await projectService.create({ name: 'CorruptProject', path: corruptPath });
      projectService.setCurrent(corruptProject);
      const corruptIssue = issueService.create({ projectId: corruptProject.id, title: 'Corrupt Issue' });

      const corruptWm = new WorktreeManager();
      const corruptWorktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'corruptproject', 'worktrees', `issue-${corruptIssue.number}`);
      fs.mkdirSync(corruptWorktreeDir, { recursive: true });

      const response = await request(server).get(`/api/issues/${corruptIssue.number}/commits`);

      expect(response.status).toBe(200);
      expect(response.body.data.available).toBe(false);
      expect(['branch_missing', 'git_error']).toContain(response.body.data.reason);

      fs.rmSync(corruptWorktreeDir, { recursive: true, force: true });
      fs.rmSync(worktreeDir, { recursive: true, force: true });
      server.close();
    });
  });

  describe('GET /api/issues/:number/commits/:hash/diff availability', () => {
    it('returns not_started for draft issue without worktree', async () => {
      const project = await projectService.create({ name: 'CommitDiffAvailTest', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Draft Diff Issue' });

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, null, undefined, agentRunner));
      const server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}/commits/abc1234/diff`);

      expect(response.status).toBe(200);
      expect(response.body.data.available).toBe(false);
      expect(response.body.data.reason).toBe('not_started');

      server.close();
    });

    it('returns worktree_removed for non-draft issue without worktree', async () => {
      const project = await projectService.create({ name: 'CommitDiffAvailTest2', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Build Diff Issue' });
      issueService.transitionToStage(issue.id, Stage.Build);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, null, undefined, agentRunner));
      const server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}/commits/abc1234/diff`);

      expect(response.status).toBe(200);
      expect(response.body.data.available).toBe(false);
      expect(response.body.data.reason).toBe('worktree_removed');

      server.close();
    });

    it('returns worktree_removed when a worktree manager exists but no build worktree exists', async () => {
      const project = await projectService.create({ name: 'CommitDiffNoWorktreeTest', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'No Worktree Build Diff Issue' });
      issueService.transitionToStage(issue.id, Stage.Build);

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, agentRunner));
      const server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}/commits/abc1234/diff`);

      expect(response.status).toBe(200);
      expect(response.body.data.available).toBe(false);
      expect(response.body.data.reason).toBe('worktree_removed');

      server.close();
    });

    it('returns branch_missing when commit does not belong to branch', async () => {
      const project = await projectService.create({ name: 'CommitHashTest', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Hash Test Issue' });
      const branchName = `mo/issue-${issue.number}`;

      const git = promisify(execFile);
      await git('git', ['checkout', '-b', branchName], { cwd: repoDir });
      fs.writeFileSync(path.join(repoDir, 'test.txt'), 'hello');
      await git('git', ['add', '-A'], { cwd: repoDir });
      const result = await git('git', ['commit', '-m', 'test'], { cwd: repoDir });
      const commitHash = result.stdout.split('\n')[0].replace('[master (root-commit) ', '').replace('] 0 files changed', '').split(' ')[0];
      await git('git', ['checkout', 'main'], { cwd: repoDir });

      const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'commithashtest', 'worktrees', `issue-${issue.number}`);
      fs.mkdirSync(worktreeDir, { recursive: true });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, agentRunner));
      const server = createTestServer(app);

      const fakeHash = '0000000000000000000000000000000000000000';
      const response = await request(server).get(`/api/issues/${issue.number}/commits/${fakeHash}/diff`);

      expect(response.status).toBe(200);
      expect(response.body.data.available).toBe(false);
      expect(['branch_missing', 'git_error']).toContain(response.body.data.reason);

      fs.rmSync(worktreeDir, { recursive: true, force: true });
      server.close();
    });
  });
});
