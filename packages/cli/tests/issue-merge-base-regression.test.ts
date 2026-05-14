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

describe('Issue diff merge-base semantic regression tests', () => {
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

  beforeEach(async () => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-merge-base-test-'));
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

  describe('ahead-only branch: two-dot and three-dot match', () => {
    it('returns the same files from merge-base diff as two-dot diff when branch is strictly ahead', async () => {
      const project = await projectService.create({ name: 'AheadOnlyTest', path: repoDir });
      projectService.setCurrent(project);

      const git = promisify(execFile);

      fs.writeFileSync(path.join(repoDir, 'base.txt'), 'base content');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'base commit'], { cwd: repoDir });

      const issue = issueService.create({ projectId: project.id, title: 'Ahead Only Issue' });
      const branchName = `mo/issue-${issue.number}`;

      await git('git', ['checkout', '-b', branchName], { cwd: repoDir });
      fs.writeFileSync(path.join(repoDir, 'issue-file-1.txt'), 'issue content 1');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'issue commit 1'], { cwd: repoDir });

      fs.writeFileSync(path.join(repoDir, 'issue-file-2.txt'), 'issue content 2');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'issue commit 2'], { cwd: repoDir });

      await git('git', ['checkout', 'main'], { cwd: repoDir });

      const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'aheadonlytest', 'worktrees', `issue-${issue.number}`);
      fs.mkdirSync(worktreeDir, { recursive: true });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, undefined, agentRunner));
      const server = createTestServer(app);

      const diffResponse = await request(server).get(`/api/issues/${issue.number}/diff`);

      expect(diffResponse.status).toBe(200);
      expect(diffResponse.body.success).toBe(true);
      expect(diffResponse.body.data.available).toBe(true);
      expect(diffResponse.body.data.comparison).toBe('merge-base');
      expect(diffResponse.body.data.ahead).toBe(2);
      expect(diffResponse.body.data.behind).toBe(0);

      const filePaths = diffResponse.body.data.files.map((f: any) => f.file);
      expect(filePaths).toContain('issue-file-1.txt');
      expect(filePaths).toContain('issue-file-2.txt');
      expect(filePaths).not.toContain('base.txt');

      const summaryFilesChanged = diffResponse.body.data.summary.filesChanged;
      expect(summaryFilesChanged).toBe(2);

      fs.rmSync(worktreeDir, { recursive: true, force: true });
      server.close();
    });

    it('commits API returns same filesChanged count as diff API for ahead-only branch', async () => {
      const project = await projectService.create({ name: 'AheadConsistencyTest', path: repoDir });
      projectService.setCurrent(project);

      const git = promisify(execFile);

      fs.writeFileSync(path.join(repoDir, 'base.txt'), 'base content');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'base commit'], { cwd: repoDir });

      const issue = issueService.create({ projectId: project.id, title: 'Ahead Consistency Issue' });
      const branchName = `mo/issue-${issue.number}`;

      await git('git', ['checkout', '-b', branchName], { cwd: repoDir });
      fs.writeFileSync(path.join(repoDir, 'file-a.txt'), 'a');
      fs.writeFileSync(path.join(repoDir, 'file-b.txt'), 'b');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'add two files'], { cwd: repoDir });

      await git('git', ['checkout', 'main'], { cwd: repoDir });

      const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'aheadconsistencytest', 'worktrees', `issue-${issue.number}`);
      fs.mkdirSync(worktreeDir, { recursive: true });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, undefined, agentRunner));
      const server = createTestServer(app);

      const [diffResponse, commitsResponse] = await Promise.all([
        request(server).get(`/api/issues/${issue.number}/diff`),
        request(server).get(`/api/issues/${issue.number}/commits`),
      ]);

      expect(diffResponse.status).toBe(200);
      expect(commitsResponse.status).toBe(200);
      expect(diffResponse.body.data.available).toBe(true);
      expect(commitsResponse.body.data.available).toBe(true);

      expect(diffResponse.body.data.summary.filesChanged).toBe(commitsResponse.body.data.summary.filesChanged);
      expect(diffResponse.body.data.files.map((f: any) => f.file).sort()).toEqual(
        commitsResponse.body.data.summary.filesChanged === 2 ? ['file-a.txt', 'file-b.txt'] : []
      );

      fs.rmSync(worktreeDir, { recursive: true, force: true });
      server.close();
    });
  });

  describe('ahead-plus-behind branch: merge-base excludes base-only files', () => {
    it('excludes base-only files from diff when branch is ahead and behind base', async () => {
      const project = await projectService.create({ name: 'AheadBehindTest', path: repoDir });
      projectService.setCurrent(project);

      const git = promisify(execFile);

      fs.writeFileSync(path.join(repoDir, 'base-file.txt'), 'base content');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'base commit on main'], { cwd: repoDir });

      const issue = issueService.create({ projectId: project.id, title: 'Ahead Behind Issue' });
      const branchName = `mo/issue-${issue.number}`;

      await git('git', ['checkout', '-b', branchName], { cwd: repoDir });
      fs.writeFileSync(path.join(repoDir, 'issue-only.txt'), 'issue only');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'issue commit'], { cwd: repoDir });

      await git('git', ['checkout', 'main'], { cwd: repoDir });
      await git('git', ['merge', branchName, '-m', 'merge issue into main'], { cwd: repoDir });

      await git('git', ['checkout', branchName], { cwd: repoDir });
      fs.writeFileSync(path.join(repoDir, 'issue-only.txt'), 'updated issue content');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'update issue-only after merge'], { cwd: repoDir });
      await git('git', ['merge', 'main', '-m', 'merge main into issue'], { cwd: repoDir });

      await git('git', ['checkout', 'main'], { cwd: repoDir });
      fs.writeFileSync(path.join(repoDir, 'main-only.txt'), 'main only file');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'main-only commit after merge'], { cwd: repoDir });

      await git('git', ['checkout', branchName], { cwd: repoDir });

      const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'aheadbehindtest', 'worktrees', `issue-${issue.number}`);
      fs.mkdirSync(worktreeDir, { recursive: true });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, undefined, agentRunner));
      const server = createTestServer(app);

      const diffResponse = await request(server).get(`/api/issues/${issue.number}/diff`);

      expect(diffResponse.status).toBe(200);
      expect(diffResponse.body.data.available).toBe(true);

      const filePaths = diffResponse.body.data.files.map((f: any) => f.file);
      expect(filePaths).toContain('issue-only.txt');
      expect(filePaths).not.toContain('main-only.txt');
      expect(filePaths).not.toContain('base-file.txt');

      fs.rmSync(worktreeDir, { recursive: true, force: true });
      server.close();
    });

    it('files-changed count from merge-base diff does not include base-only changes', async () => {
      const project = await projectService.create({ name: 'Regression199Test', path: repoDir });
      projectService.setCurrent(project);

      const git = promisify(execFile);

      fs.writeFileSync(path.join(repoDir, 'base.txt'), 'base');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'base'], { cwd: repoDir });

      const issue = issueService.create({ projectId: project.id, title: 'Regression 199 Issue' });
      const branchName = `mo/issue-${issue.number}`;

      await git('git', ['checkout', '-b', branchName], { cwd: repoDir });
      fs.writeFileSync(path.join(repoDir, 'issue-file.txt'), 'issue content');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'issue commit'], { cwd: repoDir });

      await git('git', ['checkout', 'main'], { cwd: repoDir });
      await git('git', ['merge', branchName, '-m', 'merge'], { cwd: repoDir });

      await git('git', ['checkout', branchName], { cwd: repoDir });
      fs.writeFileSync(path.join(repoDir, 'issue-file.txt'), 'updated issue content');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'update issue after merge'], { cwd: repoDir });
      await git('git', ['merge', 'main', '-m', 'merge main'], { cwd: repoDir });

      await git('git', ['checkout', 'main'], { cwd: repoDir });

      fs.writeFileSync(path.join(repoDir, 'base-only-1.txt'), 'base only 1');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'base-only-1'], { cwd: repoDir });

      fs.writeFileSync(path.join(repoDir, 'base-only-2.txt'), 'base only 2');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'base-only-2'], { cwd: repoDir });

      fs.writeFileSync(path.join(repoDir, 'base-only-3.txt'), 'base only 3');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'base-only-3'], { cwd: repoDir });

      await git('git', ['checkout', branchName], { cwd: repoDir });
      await git('git', ['merge', 'main', '-m', 'merge main into issue again'], { cwd: repoDir });

      fs.writeFileSync(path.join(repoDir, 'new-issue-file.txt'), 'new issue file');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'new issue file commit'], { cwd: repoDir });

      await git('git', ['checkout', 'main'], { cwd: repoDir });

      const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'regression199test', 'worktrees', `issue-${issue.number}`);
      fs.mkdirSync(worktreeDir, { recursive: true });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, undefined, agentRunner));
      const server = createTestServer(app);

      const diffResponse = await request(server).get(`/api/issues/${issue.number}/diff`);

      expect(diffResponse.status).toBe(200);
      expect(diffResponse.body.data.available).toBe(true);

      const filePaths = diffResponse.body.data.files.map((f: any) => f.file);
      expect(filePaths).toContain('new-issue-file.txt');
      expect(filePaths).toContain('issue-file.txt');
      expect(filePaths).not.toContain('base-only-1.txt');
      expect(filePaths).not.toContain('base-only-2.txt');
      expect(filePaths).not.toContain('base-only-3.txt');

      const summaryFilesChanged = diffResponse.body.data.summary.filesChanged;
      expect(summaryFilesChanged).toBe(filePaths.length);
      expect(summaryFilesChanged).toBeLessThan(6);

      fs.rmSync(worktreeDir, { recursive: true, force: true });
      server.close();
    });
  });

  describe('commit-scoped diff does not change issue-level semantics', () => {
    it('commit diff remains single-commit scoped and does not redefine issue-level files changed', async () => {
      const project = await projectService.create({ name: 'CommitScopeTest', path: repoDir });
      projectService.setCurrent(project);

      const git = promisify(execFile);

      fs.writeFileSync(path.join(repoDir, 'base.txt'), 'base');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'base'], { cwd: repoDir });

      const issue = issueService.create({ projectId: project.id, title: 'Commit Scope Issue' });
      const branchName = `mo/issue-${issue.number}`;

      await git('git', ['checkout', '-b', branchName], { cwd: repoDir });
      fs.writeFileSync(path.join(repoDir, 'file-1.txt'), 'content1');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'commit 1 - adds file-1'], { cwd: repoDir });

      fs.writeFileSync(path.join(repoDir, 'file-2.txt'), 'content2');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'commit 2 - adds file-2'], { cwd: repoDir });

      await git('git', ['checkout', 'main'], { cwd: repoDir });

      const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'commitscopetest', 'worktrees', `issue-${issue.number}`);
      fs.mkdirSync(worktreeDir, { recursive: true });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, undefined, agentRunner));
      const server = createTestServer(app);

      const diffResponse = await request(server).get(`/api/issues/${issue.number}/diff`);
      expect(diffResponse.status).toBe(200);
      expect(diffResponse.body.data.summary.filesChanged).toBe(2);

      const commitsResponse = await request(server).get(`/api/issues/${issue.number}/commits`);
      expect(commitsResponse.status).toBe(200);
      expect(commitsResponse.body.data.commits).toHaveLength(2);

      const firstCommitHash = commitsResponse.body.data.commits[1].hash;
      const commitDiffResponse = await request(server).get(`/api/issues/${issue.number}/commits/${firstCommitHash}/diff`);
      expect(commitDiffResponse.status).toBe(200);
      expect(commitDiffResponse.body.data.available).toBe(true);
      expect(commitDiffResponse.body.data.hash).toBe(firstCommitHash);
      expect(commitDiffResponse.body.data.diff).toContain('file-1.txt');

      const diffAfterCommitDiff = await request(server).get(`/api/issues/${issue.number}/diff`);
      expect(diffAfterCommitDiff.body.data.summary.filesChanged).toBe(2);

      fs.rmSync(worktreeDir, { recursive: true, force: true });
      server.close();
    });
  });

  describe('comparison metadata presence and correctness', () => {
    it('diff response includes all comparison metadata fields', async () => {
      const project = await projectService.create({ name: 'MetadataTest', path: repoDir });
      projectService.setCurrent(project);

      const git = promisify(execFile);

      fs.writeFileSync(path.join(repoDir, 'base.txt'), 'base');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'base'], { cwd: repoDir });

      const issue = issueService.create({ projectId: project.id, title: 'Metadata Issue' });
      const branchName = `mo/issue-${issue.number}`;

      await git('git', ['checkout', '-b', branchName], { cwd: repoDir });
      fs.writeFileSync(path.join(repoDir, 'issue.txt'), 'issue');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'issue'], { cwd: repoDir });

      await git('git', ['checkout', 'main'], { cwd: repoDir });

      const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'metadatatest', 'worktrees', `issue-${issue.number}`);
      fs.mkdirSync(worktreeDir, { recursive: true });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, undefined, agentRunner));
      const server = createTestServer(app);

      const diffResponse = await request(server).get(`/api/issues/${issue.number}/diff`);

      expect(diffResponse.status).toBe(200);
      expect(diffResponse.body.data.base).toBe('main');
      expect(diffResponse.body.data.head).toBe(branchName);
      expect(typeof diffResponse.body.data.mergeBase).toBe('string');
      expect(diffResponse.body.data.mergeBase.length).toBeGreaterThan(0);
      expect(typeof diffResponse.body.data.ahead).toBe('number');
      expect(typeof diffResponse.body.data.behind).toBe('number');
      expect(typeof diffResponse.body.data.canFastForward).toBe('boolean');
      expect(diffResponse.body.data.comparison).toBe('merge-base');

      fs.rmSync(worktreeDir, { recursive: true, force: true });
      server.close();
    });

    it('commits response includes the same comparison metadata as diff response', async () => {
      const project = await projectService.create({ name: 'ConsistentMetadataTest', path: repoDir });
      projectService.setCurrent(project);

      const git = promisify(execFile);

      fs.writeFileSync(path.join(repoDir, 'base.txt'), 'base');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'base'], { cwd: repoDir });

      const issue = issueService.create({ projectId: project.id, title: 'Consistent Metadata Issue' });
      const branchName = `mo/issue-${issue.number}`;

      await git('git', ['checkout', '-b', branchName], { cwd: repoDir });
      fs.writeFileSync(path.join(repoDir, 'issue.txt'), 'issue');
      await git('git', ['add', '-A'], { cwd: repoDir });
      await git('git', ['commit', '-m', 'issue'], { cwd: repoDir });

      await git('git', ['checkout', 'main'], { cwd: repoDir });

      const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'consistentmetadatatest', 'worktrees', `issue-${issue.number}`);
      fs.mkdirSync(worktreeDir, { recursive: true });

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const wm = new WorktreeManager();

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, undefined, agentRunner));
      const server = createTestServer(app);

      const [diffResponse, commitsResponse] = await Promise.all([
        request(server).get(`/api/issues/${issue.number}/diff`),
        request(server).get(`/api/issues/${issue.number}/commits`),
      ]);

      expect(diffResponse.body.data.base).toBe(commitsResponse.body.data.base);
      expect(diffResponse.body.data.head).toBe(commitsResponse.body.data.head);
      expect(diffResponse.body.data.mergeBase).toBe(commitsResponse.body.data.mergeBase);
      expect(diffResponse.body.data.ahead).toBe(commitsResponse.body.data.ahead);
      expect(diffResponse.body.data.behind).toBe(commitsResponse.body.data.behind);
      expect(diffResponse.body.data.canFastForward).toBe(commitsResponse.body.data.canFastForward);
      expect(diffResponse.body.data.comparison).toBe(commitsResponse.body.data.comparison);

      fs.rmSync(worktreeDir, { recursive: true, force: true });
      server.close();
    });
  });

  describe('unavailable states return correct reason codes', () => {
    it('returns not_started for draft issue with no worktree on diff', async () => {
      const project = await projectService.create({ name: 'NotStartedDiffTest', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Draft Issue' });

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, null, undefined, agentRunner));
      const server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}/diff`);

      expect(response.status).toBe(200);
      expect(response.body.data.available).toBe(false);
      expect(response.body.data.reason).toBe('not_started');

      server.close();
    });

    it('returns not_started for draft issue with no worktree on commits', async () => {
      const project = await projectService.create({ name: 'NotStartedCommitsTest', path: repoDir });
      projectService.setCurrent(project);

      const issue = issueService.create({ projectId: project.id, title: 'Draft Issue' });

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

    it('returns worktree_removed for non-draft issue with no worktree on diff', async () => {
      const project = await projectService.create({ name: 'WorktreeRemovedDiffTest', path: repoDir });
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
      expect(response.body.data.available).toBe(false);
      expect(response.body.data.reason).toBe('worktree_removed');

      server.close();
    });
  });
});