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
import { createProjectRoutes } from '../src/api/projects';
import { createIssueRoutes } from '../src/api/issues';
import { Stage, IssueStatus, MergeState } from '../src/types';
import { createStatusRoutes } from '../src/api/status';
import { createConfigRoutes } from '../src/api/config';
import { StageExecutionRepo } from '../src/db/stage-execution-repo';
import { WorkflowRunRepo } from '../src/db/workflow-run-repo';
import { StageStateService } from '../src/services/stage-state-service';
import { WorkflowRunService } from '../src/services/workflow-run-service';
import { WorkflowApplicationService } from '../src/services/workflow-application-service';
import { IssuePrerequisiteService } from '../src/services/issue-prerequisite-service';
import { WorktreeManager } from '../src/git/worktree-manager';
import { slugify } from '../src/utils/slugify';

const execFileAsync = promisify(execFile);

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

function completePlanToApproval(workflowApplicationService: WorkflowApplicationService, issueId: string): void {
  for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
    workflowApplicationService.completeTask({ issueId, stage: Stage.Plan, taskId, result: { status: 'completed' } });
  }
  for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
    workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Plan, result: { name: checkName, status: 'pass' } });
  }
}

function completeCheckToApproval(workflowApplicationService: WorkflowApplicationService, issueId: string, snapshotSha: string): void {
  workflowApplicationService.approveStage({ issueId, stage: Stage.Plan, approval: { output: { approved: true } } });
  workflowApplicationService.materializeTasks({ issueId, stage: Stage.Build, tasks: [{ id: 'T-001', title: 'Build task', order: 1 }] });
  workflowApplicationService.completeTask({ issueId, stage: Stage.Build, taskId: 'T-001', result: { status: 'completed' } });
  workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Build, result: { name: 'health:build', status: 'pass' } });
  workflowApplicationService.completeTask({ issueId, stage: Stage.Check, taskId: 'ai-review', result: { status: 'completed' } });
  workflowApplicationService.recordCheckResult({
    issueId,
    stage: Stage.Check,
    result: {
      name: 'health:check',
      status: 'pass',
      output: {
        checkName: 'health:check',
        status: 'pass',
        candidateHeadSha: snapshotSha,
        command: 'npm run build && npm test',
        duration: 1,
        summary: 'Verification passed',
      },
    },
  });
  workflowApplicationService.recordCheckResult({
    issueId,
    stage: Stage.Check,
    result: { name: 'review-passed', status: 'pass', output: { verdict: 'PASS', snapshotSha } },
  });
  workflowApplicationService.recordCheckResult({
    issueId,
    stage: Stage.Check,
    result: {
      name: 'merge-ready',
      status: 'pass',
      output: {
        kind: 'merge-ready',
        targetBranch: 'main',
        strategy: 'squash',
        baseSha: snapshotSha,
        candidateHeadSha: snapshotSha,
        mergeBaseSha: snapshotSha,
        canMerge: true,
        conflictFiles: [],
        checkedAt: new Date().toISOString(),
      },
    },
  });
}

function mohistWorktreesPath(home: string, projectName: string): string {
  return path.join(home, '.mohist', 'projects', slugify(projectName), 'worktrees');
}

async function initGitRepo(dir: string, email = 'test@test.com', name = 'Test User'): Promise<void> {
  await execFileAsync('git', ['init'], { cwd: dir });
  await execFileAsync('git', ['config', 'user.email', email], { cwd: dir });
  await execFileAsync('git', ['config', 'user.name', name], { cwd: dir });
  await execFileAsync('git', ['commit', '--allow-empty', '-m', 'initial'], { cwd: dir });
  try {
    await execFileAsync('git', ['checkout', '-b', 'main'], { cwd: dir });
  } catch {
    try {
      await execFileAsync('git', ['checkout', 'main'], { cwd: dir });
    } catch {
      // main may already be current
    }
  }
}

async function createFile(dir: string, filePath: string, content: string): Promise<void> {
  const fullPath = path.join(dir, filePath);
  fs.mkdirSync(path.dirname(fullPath), { recursive: true });
  fs.writeFileSync(fullPath, content, 'utf-8');
}

async function gitCommit(dir: string, message: string): Promise<string> {
  await execFileAsync('git', ['add', '.'], { cwd: dir });
  await execFileAsync('git', ['commit', '-m', message], { cwd: dir });
  const { stdout } = await execFileAsync('git', ['rev-parse', 'HEAD'], { cwd: dir });
  return stdout.trim();
}

async function getGitSha(repoPath: string, ref: string): Promise<string> {
  const { stdout } = await execFileAsync('git', ['rev-parse', ref], { cwd: repoPath });
  return stdout.trim();
}

async function getMergeBase(repoPath: string, branch1: string, branch2: string): Promise<string> {
  const { stdout } = await execFileAsync('git', ['merge-base', branch1, branch2], { cwd: repoPath });
  return stdout.trim();
}

async function setupGitProject(tmpDir: string): Promise<{ projectPath: string; projectName: string }> {
  const projectPath = path.join(tmpDir, 'project');
  fs.mkdirSync(projectPath, { recursive: true });
  await initGitRepo(projectPath);
  await createFile(projectPath, 'src/foo.ts', 'original content\n');
  await gitCommit(projectPath, 'initial commit');
  const projectName = 'project';
  fs.mkdirSync(mohistWorktreesPath(tmpDir, projectName), { recursive: true });
  return { projectPath, projectName };
}

async function createIssueWorktree(
  tmpDir: string,
  projectPath: string,
  projectName: string,
  issueNumber: number,
  fileContent: string,
): Promise<{ worktreePath: string; candidateHead: string }> {
  const branch = `mo/issue-${issueNumber}`;
  const worktreePath = path.join(mohistWorktreesPath(tmpDir, projectName), `issue-${issueNumber}`);
  await execFileAsync('git', ['worktree', 'add', '-b', branch, worktreePath, 'main'], { cwd: projectPath });
  await createFile(worktreePath, 'src/foo.ts', fileContent);
  const candidateHead = await gitCommit(worktreePath, `issue-${issueNumber} commit`);
  return { worktreePath, candidateHead };
}

async function makeConflictingCommit(projectPath: string, fileContent: string): Promise<string> {
  await createFile(projectPath, 'src/foo.ts', fileContent);
  return gitCommit(projectPath, 'main conflicting commit');
}

async function createMergeReadyApprovalFixture(
  tmpDir: string,
  db: DatabaseManager,
  projectService: ProjectService,
  issueService: IssueService,
  stateManager: StateManager,
  snapshotOverrides: Partial<Record<string, unknown>> = {},
): Promise<{
  issue: ReturnType<IssueService['create']>;
  projectPath: string;
  projectName: string;
  baseSha: string;
  candidateHeadSha: string;
  mergeBaseSha: string;
  workflowRunService: WorkflowRunService;
  stageStateService: StageStateService;
}> {
  const { projectPath, projectName } = await setupGitProject(tmpDir);
  await createIssueWorktree(tmpDir, projectPath, projectName, 1, 'issue-1 content\n');

  const project = await projectService.create({ name: projectName, path: projectPath });
  projectService.setCurrent(project);
  const issue = issueService.create({ projectId: project.id, title: 'Check Approval Ready Issue' });
  issueService.transitionToStage(issue.id, Stage.Check);
  issueService.setStatus(issue.id, IssueStatus.Active);

  const workflowRunService = new WorkflowRunService(db);
  const workflowApplicationService = new WorkflowApplicationService(db);
  const stageStateService = new StageStateService(db);
  workflowRunService.startRun(issue.id, issue.number);
  completePlanToApproval(workflowApplicationService, issue.id);
  const candidateHeadSha = await getGitSha(projectPath, `mo/issue-${issue.number}`);
  completeCheckToApproval(workflowApplicationService, issue.id, candidateHeadSha);

  const execution = stateManager.getStageExecutionRepo().create(issue.id, Stage.Check);
  stateManager.getStageExecutionRepo().updateCheckResults(execution.id, [
    {
      name: 'ai-review',
      status: 'pass',
      output: {
        verdict: 'PASS',
        reviewReport: '# Review\n<promise>PASS</promise>',
        snapshotSha: candidateHeadSha,
      },
    },
  ]);
  stateManager.getStageExecutionRepo().updateStatus(execution.id, 'awaiting-approval');

  const checkSuiteRepo = stateManager.getCheckSuiteRepo();
  const suite = checkSuiteRepo.create({ issueId: issue.id, snapshotSha: candidateHeadSha });
  checkSuiteRepo.updateChecks(suite.id, 'review-passed', { status: 'passed' });
  checkSuiteRepo.updateChecks(suite.id, 'merge-ready', { status: 'passed' });
  checkSuiteRepo.updateStatus(suite.id, 'awaiting-approval');

  const baseSha = await getGitSha(projectPath, 'main');
  const mergeBaseSha = await getMergeBase(projectPath, 'main', `mo/issue-${issue.number}`);

  stateManager.getIssueRepo().setApprovalState(issue.id, {
    stage: Stage.Check,
    status: 'awaiting',
      output: {
        snapshotSha: candidateHeadSha,
        result: 'PASS',
        verificationEvidence: {
          checkName: 'health:check',
          status: 'pass',
          candidateHeadSha,
          command: 'npm run build && npm test',
          duration: 1,
          summary: 'Verification passed',
        },
        mergeReadySnapshot: {
        kind: 'merge-ready',
        strategy: 'squash',
        targetBranch: 'main',
        baseSha,
        candidateHeadSha,
        mergeBaseSha,
        canMerge: true,
        conflictFiles: [],
        checkedAt: new Date().toISOString(),
        ...snapshotOverrides,
      },
    },
    requestedAt: new Date().toISOString(),
  });

  return { issue, projectPath, projectName, baseSha, candidateHeadSha, mergeBaseSha, workflowRunService, stageStateService };
}

describe('API Routes', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let configRepo: ConfigRepo;
  let projectService: ProjectService;
  let issueService: IssueService;
  let configService: ConfigService;
  let stateManager: StateManager;
  let savedApiKeys: Record<string, string | undefined> = {};

  beforeEach(() => {
    savedApiKeys = {};
    for (const key of Object.keys(process.env)) {
      if (key.endsWith('_API_KEY')) {
        savedApiKeys[key] = process.env[key];
        delete process.env[key];
      }
    }

    db = new DatabaseManager({ inMemory: true });
    
    stateManager = new StateManager(db);
    
    projectRepo = stateManager.getProjectRepo();
    issueRepo = stateManager.getIssueRepo();
    configRepo = stateManager.getConfigRepo();
    
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();
    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);
    configService = new ConfigService(configRepo);
  });

  afterEach(() => {
    db.close();
    for (const [key, val] of Object.entries(savedApiKeys)) {
      if (val === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = val;
      }
    }
  });

  describe('Project Routes', () => {
    let server: http.Server;

    beforeEach(() => {
      const app = new Hono();
      app.route('/api/projects', createProjectRoutes(projectService));
      server = createTestServer(app);
    });

    describe('POST /api/projects', () => {
      it('should create a project', async () => {
        const response = await request(server)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/test/path' });

        expect(response.status).toBe(201);
        expect(response.body.success).toBe(true);
        expect(response.body.data.name).toBe('Test Project');
        expect(response.body.data.path).toBe('/test/path');
      });

      it('should require name and path', async () => {
        const response = await request(server)
          .post('/api/projects')
          .send({ name: 'Test Project' });

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('required');
      });

      it('should reject duplicate project name', async () => {
        await request(server)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/test/path' });

        const response = await request(server)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/other/path' });

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('already exists');
      });
    });

    describe('GET /api/projects', () => {
      it('should list projects', async () => {
        await request(server)
          .post('/api/projects')
          .send({ name: 'Project 1', path: '/path/1' });
        await request(server)
          .post('/api/projects')
          .send({ name: 'Project 2', path: '/path/2' });

        const response = await request(server).get('/api/projects');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data).toHaveLength(2);
      });
    });

    describe('GET /api/projects/:name', () => {
      it('should return project details', async () => {
        await request(server)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/test/path' });

        const response = await request(server).get('/api/projects/Test Project');

        expect(response.status).toBe(200);
        expect(response.body.data.name).toBe('Test Project');
      });

      it('should return 404 for non-existent project', async () => {
        const response = await request(server).get('/api/projects/NonExistent');

        expect(response.status).toBe(404);
      });
    });

    describe('DELETE /api/projects/:name', () => {
      it('should delete project', async () => {
        await request(server)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/test/path' });

        const response = await request(server).delete('/api/projects/Test Project');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
      });
    });

    describe('POST /api/projects/:name/use', () => {
      it('should set current project', async () => {
        await request(server)
          .post('/api/projects')
          .send({ name: 'Test Project', path: '/test/path' });

        const response = await request(server).post('/api/projects/Test Project/use');

        expect(response.status).toBe(200);
        expect(response.body.data.name).toBe('Test Project');
      });
    });
  });

  describe('Issue Routes', () => {
    let server: http.Server;
    let projectId: string;
    let stageExecutionRepo: StageExecutionRepo;

    beforeEach(async () => {
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, projectRepo, undefined, stateManager.getIssueTaskQueueRepo());
      stageExecutionRepo = stateManager.getStageExecutionRepo();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, agentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stageExecutionRepo));
      server = createTestServer(app);
      
      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectId = project.id;
      projectService.setCurrent(project);
    });

    describe('POST /api/issues', () => {
      it('should create an issue', async () => {
        const response = await request(server)
          .post('/api/issues')
          .send({ title: 'Test Issue', body: 'Test body' });

        expect(response.status).toBe(201);
        expect(response.body.success).toBe(true);
        expect(response.body.data.title).toBe('Test Issue');
        expect(response.body.data.number).toBe(1);
      });

      it('should require title', async () => {
        const response = await request(server)
          .post('/api/issues')
          .send({ body: 'Test body' });

        expect(response.status).toBe(400);
      });

      it('should return error when no current project', async () => {
        projectService.clearCurrent();
        
        const response = await request(server)
          .post('/api/issues')
          .send({ title: 'Test Issue' });

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('No active project');
      });
    });

    describe('GET /api/issues', () => {
      it('should list issues', async () => {
        await issueService.create({ projectId, title: 'Issue 1' });
        await issueService.create({ projectId, title: 'Issue 2' });

        const response = await request(server).get('/api/issues');

        expect(response.status).toBe(200);
        expect(response.body.data).toHaveLength(2);
      });

      it('should filter by stage', async () => {
        await issueService.create({ projectId, title: 'Test' });
        issueService.transitionToStageByNumber(projectId, 1, 'plan' as any);

        const response = await request(server).get('/api/issues?stage=plan');

        expect(response.status).toBe(200);
        expect(response.body.data).toHaveLength(1);
      });
    });

    describe('GET /api/issues/:number', () => {
      it('should return issue details', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).get('/api/issues/1');

        expect(response.status).toBe(200);
        expect(response.body.data.number).toBe(1);
        expect(response.body.data.title).toBe('Test Issue');
      });

      it('should return 404 for non-existent issue', async () => {
        const response = await request(server).get('/api/issues/999');

        expect(response.status).toBe(404);
      });
    });

    describe('WorkflowRun-backed progress endpoints', () => {
      function createWorkflowRunProgressServer(stageStateService: StageStateService, workflowRunService: WorkflowRunService) {
        const app = new Hono();
        app.route('/api/issues', createIssueRoutes(
          issueService,
          projectService,
          stateManager,
          undefined,
          undefined,
          undefined,
          undefined,
          undefined,
          undefined,
          undefined,
          undefined,
          undefined,
          undefined,
          undefined,
          undefined,
          undefined,
          stageStateService,
          workflowRunService,
        ));
        return createTestServer(app);
      }

      it('returns aggregate-backed WorkflowRun with approval, failure, and Integrate delivery metadata', async () => {
        const issue = issueService.create({ projectId, title: 'WorkflowRun API Issue' });
        const stageStateService = new StageStateService(db);
        const workflowRunService = new WorkflowRunService(db);
        const run = workflowRunService.startRun(issue.id, issue.number);

        const now = new Date().toISOString();
        db.run(
          `UPDATE workflow_stage_runs
           SET status = 'awaiting-approval', approval_status = 'awaiting', approval_output = ?, approval_requested_at = ?, updated_at = ?
           WHERE workflow_run_id = ? AND stage = ?`,
          [JSON.stringify({ snapshotSha: 'abc123' }), '2026-01-01T00:00:00Z', now, run.id, Stage.Plan],
        );
        db.run(`UPDATE workflow_stage_runs SET status = 'failed', completed_at = ?, updated_at = ? WHERE workflow_run_id = ? AND stage = ?`, [now, now, run.id, Stage.Integrate]);
        db.run(
          `UPDATE workflow_tasks SET status = 'completed', output = ?, completed_at = ?, updated_at = ? WHERE workflow_run_id = ? AND task_id = ?`,
          [JSON.stringify({ targetBranch: 'main', baseSha: 'base-sha', candidateHeadSha: 'head-sha', landedSha: 'landed-sha', rebased: false }), now, now, run.id, 'integrate:merge'],
        );
        db.run(
          `UPDATE workflow_checks SET status = 'failed', message = ?, run_count = ?, last_run_at = ?, updated_at = ? WHERE workflow_run_id = ? AND check_name = ?`,
          ['typecheck failed', 1, now, now, run.id, 'health:integrate'],
        );

        const apiServer = createWorkflowRunProgressServer(stageStateService, workflowRunService);
        const response = await request(apiServer).get(`/api/issues/${issue.number}/workflow-run`);

        expect(response.status).toBe(200);
        const plan = response.body.data.stageRuns.find((s: any) => s.stage === 'plan');
        expect(plan.approval).toMatchObject({ status: 'awaiting', output: { snapshotSha: 'abc123' } });
        const integrate = response.body.data.stageRuns.find((s: any) => s.stage === 'integrate');
        expect(integrate.deliveryMetadata.merge).toMatchObject({ targetBranch: 'main', candidateHeadSha: 'head-sha', landedSha: 'landed-sha' });
        expect(integrate.deliveryMetadata.health).toMatchObject({ status: 'failed', message: 'typecheck failed' });
        expect(integrate.failure).toMatchObject({ reason: 'post-merge-health-failed', checkName: 'health:integrate' });
        expect(response.body.data.workflowDefinition).toMatchObject({
          workflowId: 'mohist/default',
          source: { type: 'builtin', id: 'mohist/default' },
        });
        const proposal = plan.tasks.find((task: any) => task.taskId === 'proposal');
        expect(proposal.origin).toEqual({ source: 'builtin', uses: 'mohist/agent' });
        const integrateHealth = integrate.checks.find((check: any) => check.checkName === 'health:integrate');
        expect(integrateHealth.origin).toEqual({ source: 'builtin', uses: 'mohist/health-gate' });
      });

      it('stage-state projects from WorkflowRun and ignores legacy evidence when a run exists', async () => {
        const issue = issueService.create({ projectId, title: 'Stage State WorkflowRun Issue' });
        const stageStateService = new StageStateService(db);
        const workflowRunService = new WorkflowRunService(db);
        const run = workflowRunService.startRun(issue.id, issue.number);
        const now = new Date().toISOString();
        db.run(
          `INSERT INTO workflow_tasks
           (id, workflow_run_id, stage_run_id, task_id, title, status, task_order, attempts, duration, artifacts, created_at, updated_at)
           VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
          [`${run.id}/build/T-001`, run.id, `${run.id}/build`, 'T-001', 'WorkflowRun task', 'pending', 1, 0, 0, '[]', now, now],
        );
        db.run(
          `INSERT INTO workflow_tasks
           (id, workflow_run_id, stage_run_id, task_id, title, status, task_order, attempts, duration, artifacts, reason, caused_by_type, caused_by_check_name, created_at, updated_at)
           VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
          [`${run.id}/build/fix-build-health`, run.id, `${run.id}/build`, 'fix-build-health', 'Fix build health', 'running', 0, 0, 0, '[]', 'Added after build health failed', 'check-failure', 'health:build', now, now],
        );
        stageExecutionRepo.create(issue.id, Stage.Build);
        db.run(
          `INSERT INTO workflow_log (id, issue_id, session_id, event_type, data, created_at)
           VALUES (?, ?, ?, ?, ?, ?)`,
          ['log-evidence', issue.id, null, 'task_completed', JSON.stringify({ taskId: 'T-LOG', title: 'Log task' }), now],
        );

        const apiServer = createWorkflowRunProgressServer(stageStateService, workflowRunService);
        const response = await request(apiServer).get(`/api/issues/${issue.number}/stage-state`);

        expect(response.status).toBe(200);
        const build = response.body.data.stages.find((s: any) => s.stage === 'build');
        expect(build.tasks.map((t: any) => t.taskId)).toEqual(['fix-build-health', 'T-001']);
        expect(build.tasks.find((t: any) => t.taskId === 'fix-build-health').causedBy).toMatchObject({ checkName: 'health:build' });
        expect(JSON.stringify(build)).not.toContain('T-LOG');
      });

      it('stage-state keeps legacy fallback available when no WorkflowRun exists', async () => {
        const issue = issueService.create({ projectId, title: 'Legacy Fallback Issue' });
        const stageStateService = new StageStateService(db);
        stageStateService.ensureStage(issue.id, Stage.Build);
        stageStateService.upsertTask(issue.id, Stage.Build, {
          taskId: 'T-001',
          title: 'Legacy task',
          status: 'completed',
        });

        const apiServer = createWorkflowRunProgressServer(stageStateService, new WorkflowRunService(db));
        const response = await request(apiServer).get(`/api/issues/${issue.number}/stage-state`);

        expect(response.status).toBe(200);
        const build = response.body.data.stages.find((s: any) => s.stage === 'build');
        expect(build.tasks).toEqual(expect.arrayContaining([expect.objectContaining({ taskId: 'T-001', title: 'Legacy task' })]));
      });
    });

    describe('POST /api/issues/:number/start', () => {
      it('should enqueue start-pipeline for an issue', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).post('/api/issues/1/start');

        expect(response.status).toBe(202);
        expect(response.body.success).toBe(true);
        expect(response.body.data.taskId).toBeDefined();
        expect(response.body.data.status).toBeDefined();
      });
    });

    describe('POST /api/issues/:number/approve', () => {
      it('should return 400 when no pending gate in memory or DB', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).post('/api/issues/1/approve');

        expect(response.status).toBe(400);
        expect(response.body.error).toMatch(/No pending approval/);
      });

      it('approves Plan through WorkflowRun when hasPendingGate returns false but DB has awaiting projection', async () => {
        const issue = issueService.create({ projectId, title: 'Awaiting Issue' });
        const stageStateService = new StageStateService(db);
        const workflowRunService = new WorkflowRunService(db);
        const workflowApplicationService = new WorkflowApplicationService(db);
        workflowRunService.startRun(issue.id, issue.number);
        completePlanToApproval(workflowApplicationService, issue.id);

        const refreshedIssue = issueService.getByNumber(projectId, 1);
        expect(refreshedIssue?.approvalState?.status).toBe('awaiting');

        const approveApp = new Hono();
        const approveAgentRunner = new AgentRunnerService(new EventBus(), undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
        approveApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, approveAgentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stageExecutionRepo, undefined, stageStateService, workflowRunService));
        const approveServer = createTestServer(approveApp);

        const response = await request(approveServer).post('/api/issues/1/approve');

        expect(response.status).toBe(202);
        expect(response.body.success).toBe(true);
        const updatedStageState = stageStateService.getStageState(issue.id, Stage.Plan);
        expect(updatedStageState?.approval?.status).toBe('approved');
        expect(updatedStageState?.approval?.respondedAt).toBeTruthy();
        expect(workflowRunService.getActiveRunForIssue(issue.id)?.stageRuns.find(stage => stage.stage === Stage.Build)?.status).toBe('running');
      });

      it('Check approval should reject when authoritative PASS review is missing', async () => {
        const issue = issueService.create({ projectId, title: 'Check Approval Issue' });
        issueService.transitionToStage(issue.id, Stage.Check);
        issueService.setStatus(issue.id, IssueStatus.Active);

        const issueRepo = stateManager.getIssueRepo();
        issueRepo.setApprovalState(issue.id, {
          stage: Stage.Check,
          status: 'awaiting',
          output: { test: true },
          requestedAt: new Date().toISOString(),
        });

        const approveApp = new Hono();
        const approveEventBus = new EventBus();
        const approveAgentRunner = new AgentRunnerService(approveEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
        const enqueueSpy = vi.spyOn(approveAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
        approveApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, approveAgentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stageExecutionRepo));
        const approveServer = createTestServer(approveApp);

        const response = await request(approveServer).post(`/api/issues/${issue.number}/approve`);

        expect(response.status).toBe(409);
        expect(response.body.success).toBe(false);
        expect(response.body.error).toContain("latest review verdict");
        expect(enqueueSpy).not.toHaveBeenCalled();
      });

      it('Check approval should transition to Integrate and enqueue resume-pipeline when authoritative PASS matches snapshot', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-approve-pass-'));
        const originalHome = process.env.HOME;

        try {
          process.env.HOME = tmpDir;
          const { issue, workflowRunService, stageStateService } = await createMergeReadyApprovalFixture(tmpDir, db, projectService, issueService, stateManager);

          const approveApp = new Hono();
          const approveEventBus = new EventBus();
          const approveAgentRunner = new AgentRunnerService(approveEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(approveAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
          approveApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, new WorktreeManager(), undefined, approveAgentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stageExecutionRepo, undefined, stageStateService, workflowRunService));
          const approveServer = createTestServer(approveApp);

          const response = await request(approveServer).post(`/api/issues/${issue.number}/approve`);

          expect(response.status).toBe(202);
          expect(response.body.success).toBe(true);
          expect(enqueueSpy).toHaveBeenCalledTimes(1);
          expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');
          const updatedStageState = stageStateService.getStageState(issue.id, Stage.Check);
          expect(updatedStageState?.approval?.status).toBe('approved');
          expect(updatedStageState?.approval?.respondedAt).toBeTruthy();
          expect(workflowRunService.getActiveRunForIssue(issue.id)?.currentStage).toBe(Stage.Integrate);
        } finally {
          process.env.HOME = originalHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      });

      it('Check approval should use WorkflowRun review when legacy stage execution is missing', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-approve-workflowrun-pass-'));
        const originalHome = process.env.HOME;

        try {
          process.env.HOME = tmpDir;
          const { issue, workflowRunService, stageStateService } = await createMergeReadyApprovalFixture(tmpDir, db, projectService, issueService, stateManager);

          const approveApp = new Hono();
          const approveEventBus = new EventBus();
          const approveAgentRunner = new AgentRunnerService(approveEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(approveAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
          approveApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, new WorktreeManager(), undefined, approveAgentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stageStateService, workflowRunService));
          const approveServer = createTestServer(approveApp);

          const response = await request(approveServer).post(`/api/issues/${issue.number}/approve`);

          expect(response.status).toBe(202);
          expect(response.body.success).toBe(true);
          expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');
          expect(workflowRunService.getActiveRunForIssue(issue.id)?.currentStage).toBe(Stage.Integrate);
        } finally {
          process.env.HOME = originalHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      });

      it('Check approval should reject WorkflowRun-only PASS when merge-ready snapshot is missing', async () => {
        const issue = issueService.create({ projectId, title: 'Check Approval Missing Merge Evidence Issue' });
        const stageStateService = new StageStateService(db);
        const workflowRunService = new WorkflowRunService(db);
        const workflowApplicationService = new WorkflowApplicationService(db);
        workflowRunService.startRun(issue.id, issue.number);
        completePlanToApproval(workflowApplicationService, issue.id);
        completeCheckToApproval(workflowApplicationService, issue.id, 'sha-pass-003');
        stateManager.getIssueRepo().setApprovalState(issue.id, {
          stage: Stage.Check,
          status: 'awaiting',
          output: { snapshotSha: 'sha-pass-003', result: 'PASS' },
          requestedAt: new Date().toISOString(),
        });

        const worktreeManager = {
          getPath: vi.fn().mockReturnValue('/tmp/worktree'),
          getHeadSha: vi.fn().mockResolvedValue('sha-pass-003'),
          isWorktreeClean: vi.fn().mockResolvedValue(true),
        } as any;

        const approveApp = new Hono();
        const approveEventBus = new EventBus();
        const approveAgentRunner = new AgentRunnerService(approveEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
        const enqueueSpy = vi.spyOn(approveAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
        approveApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, worktreeManager, undefined, approveAgentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stageExecutionRepo, undefined, stageStateService, workflowRunService));
        const approveServer = createTestServer(approveApp);

        const response = await request(approveServer).post(`/api/issues/${issue.number}/approve`);

        expect(response.status).toBe(409);
        expect(response.body.success).toBe(false);
        expect(response.body.error).toContain('merge-ready snapshot is missing');
        expect(enqueueSpy).not.toHaveBeenCalled();
        expect(workflowRunService.getActiveRunForIssue(issue.id)?.currentStage).toBe(Stage.Check);
      });

      it('Plan approval should surface the exact WorkflowRun completion guard reason through the workflow runner', async () => {
        const issue = issueService.create({ projectId, title: 'Plan Approval Guard Reason Issue' });
        const stageStateService = new StageStateService(db);
        const workflowRunService = new WorkflowRunService(db);
        const workflowApplicationService = new WorkflowApplicationService(db);
        workflowRunService.startRun(issue.id, issue.number);
        completePlanToApproval(workflowApplicationService, issue.id);

        const workflowRunRepo = new WorkflowRunRepo(db);
        const activeRun = workflowRunRepo.loadActiveAggregate(issue.id)!;
        const planStage = activeRun.stageRun(Stage.Plan);
        const healthPlan = planStage.checks.find(check => check.name === 'health:plan');
        if (!healthPlan) throw new Error('health:plan check missing');
        healthPlan.status = 'pending';
        workflowRunRepo.saveAggregate(activeRun);

        const approveApp = new Hono();
        const approveAgentRunner = new AgentRunnerService(new EventBus(), undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
        const enqueueSpy = vi.spyOn(approveAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
        approveApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, approveAgentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stageExecutionRepo, undefined, stageStateService, workflowRunService));
        const approveServer = createTestServer(approveApp);

        const response = await request(approveServer).post(`/api/issues/${issue.number}/approve`);

        expect(response.status).toBe(202);
        expect(response.body.success).toBe(true);
        expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');

        const updatedRun = workflowRunService.getActiveRunForIssue(issue.id);
        expect(updatedRun?.currentStage).toBe(Stage.Plan);
        expect(updatedRun?.stageRuns.find(stageRun => stageRun.stage === Stage.Plan)?.approvalStatus).toBe('awaiting');
      });

      it('Check approval should reject stale merge-ready base SHA', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-approve-base-'));
        const originalHome = process.env.HOME;

        try {
          process.env.HOME = tmpDir;
          const { issue, workflowRunService, stageStateService } = await createMergeReadyApprovalFixture(tmpDir, db, projectService, issueService, stateManager, {
            baseSha: '0000000000000000000000000000000000000000',
          });

          const approveApp = new Hono();
          const approveAgentRunner = new AgentRunnerService(new EventBus(), undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(approveAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
          approveApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, new WorktreeManager(), undefined, approveAgentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stateManager.getCheckSuiteRepo(), stageExecutionRepo, undefined, stageStateService, workflowRunService));
          const approveServer = createTestServer(approveApp);

          const response = await request(approveServer).post(`/api/issues/${issue.number}/approve`);

          expect(response.status).toBe(409);
          expect(response.body.error).toContain('base SHA');
          expect(enqueueSpy).not.toHaveBeenCalled();
        } finally {
          process.env.HOME = originalHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      });

      it('Check approval should reject malformed merge-ready conflictFiles evidence', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-approve-conflictfiles-'));
        const originalHome = process.env.HOME;

        try {
          process.env.HOME = tmpDir;
          const { issue, workflowRunService, stageStateService } = await createMergeReadyApprovalFixture(tmpDir, db, projectService, issueService, stateManager, {
            conflictFiles: undefined,
          });

          const approveApp = new Hono();
          const approveAgentRunner = new AgentRunnerService(new EventBus(), undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(approveAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
          approveApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, new WorktreeManager(), undefined, approveAgentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stateManager.getCheckSuiteRepo(), stageExecutionRepo, undefined, stageStateService, workflowRunService));
          const approveServer = createTestServer(approveApp);

          const response = await request(approveServer).post(`/api/issues/${issue.number}/approve`);

          expect(response.status).toBe(409);
          expect(response.body.error).toContain('conflictFiles');
          expect(enqueueSpy).not.toHaveBeenCalled();
        } finally {
          process.env.HOME = originalHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      });

      it('Check approval should reject stale merge-ready snapshot even when the issue worktree path is missing', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-approve-no-worktree-'));
        const originalHome = process.env.HOME;

        try {
          process.env.HOME = tmpDir;
          const { issue, workflowRunService, stageStateService } = await createMergeReadyApprovalFixture(tmpDir, db, projectService, issueService, stateManager, {
            baseSha: '0000000000000000000000000000000000000000',
          });
          const worktreeManager = {
            getPath: vi.fn().mockReturnValue(null),
          } as any;

          const approveApp = new Hono();
          const approveAgentRunner = new AgentRunnerService(new EventBus(), undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(approveAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
          approveApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, worktreeManager, undefined, approveAgentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stateManager.getCheckSuiteRepo(), stageExecutionRepo, undefined, stageStateService, workflowRunService));
          const approveServer = createTestServer(approveApp);

          const response = await request(approveServer).post(`/api/issues/${issue.number}/approve`);

          expect(response.status).toBe(409);
          expect(response.body.error).toContain('base SHA');
          expect(enqueueSpy).not.toHaveBeenCalled();
        } finally {
          process.env.HOME = originalHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      });

      it('Check approval should reject stale merge-ready candidate head SHA', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-approve-head-'));
        const originalHome = process.env.HOME;

        try {
          process.env.HOME = tmpDir;
          const { issue, workflowRunService, stageStateService } = await createMergeReadyApprovalFixture(tmpDir, db, projectService, issueService, stateManager, {
            candidateHeadSha: '1111111111111111111111111111111111111111',
          });

          const approveApp = new Hono();
          const approveAgentRunner = new AgentRunnerService(new EventBus(), undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(approveAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
          approveApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, new WorktreeManager(), undefined, approveAgentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stateManager.getCheckSuiteRepo(), stageExecutionRepo, undefined, stageStateService, workflowRunService));
          const approveServer = createTestServer(approveApp);

          const response = await request(approveServer).post(`/api/issues/${issue.number}/approve`);

          expect(response.status).toBe(409);
          expect(response.body.error).toContain('candidate head SHA');
          expect(enqueueSpy).not.toHaveBeenCalled();
        } finally {
          process.env.HOME = originalHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      });

      it('Check approval should reject stale merge-ready merge-base SHA', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-approve-mergebase-'));
        const originalHome = process.env.HOME;

        try {
          process.env.HOME = tmpDir;
          const { issue, workflowRunService, stageStateService } = await createMergeReadyApprovalFixture(tmpDir, db, projectService, issueService, stateManager, {
            mergeBaseSha: '2222222222222222222222222222222222222222',
          });

          const approveApp = new Hono();
          const approveAgentRunner = new AgentRunnerService(new EventBus(), undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(approveAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
          approveApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, new WorktreeManager(), undefined, approveAgentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stateManager.getCheckSuiteRepo(), stageExecutionRepo, undefined, stageStateService, workflowRunService));
          const approveServer = createTestServer(approveApp);

          const response = await request(approveServer).post(`/api/issues/${issue.number}/approve`);

          expect(response.status).toBe(409);
          expect(response.body.error).toContain('merge-base SHA');
          expect(enqueueSpy).not.toHaveBeenCalled();
        } finally {
          process.env.HOME = originalHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      });

      it('Check approval should reject stale merge-ready target branch', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-approve-target-'));
        const originalHome = process.env.HOME;

        try {
          process.env.HOME = tmpDir;
          const { issue, workflowRunService, stageStateService } = await createMergeReadyApprovalFixture(tmpDir, db, projectService, issueService, stateManager, {
            targetBranch: 'release',
          });

          const approveApp = new Hono();
          const approveAgentRunner = new AgentRunnerService(new EventBus(), undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(approveAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
          approveApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, new WorktreeManager(), undefined, approveAgentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stateManager.getCheckSuiteRepo(), stageExecutionRepo, undefined, stageStateService, workflowRunService));
          const approveServer = createTestServer(approveApp);

          const response = await request(approveServer).post(`/api/issues/${issue.number}/approve`);

          expect(response.status).toBe(409);
          expect(response.body.error).toContain('target branch');
          expect(enqueueSpy).not.toHaveBeenCalled();
        } finally {
          process.env.HOME = originalHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      });

      it('Check approval should fail closed when Git freshness validation cannot run', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-approve-gitfail-'));
        const originalHome = process.env.HOME;

        try {
          process.env.HOME = tmpDir;
          const { issue, projectName, workflowRunService, stageStateService } = await createMergeReadyApprovalFixture(tmpDir, db, projectService, issueService, stateManager);

          const failingWorktreeManager = {
            getPath: vi.fn().mockReturnValue(path.join(mohistWorktreesPath(tmpDir, projectName), `issue-${issue.number}`)),
            getHeadSha: vi.fn().mockRejectedValue(new Error('missing worktree')),
            isWorktreeClean: vi.fn().mockResolvedValue(true),
            exists: vi.fn().mockReturnValue(true),
          } as any;

          const approveApp = new Hono();
          const approveAgentRunner = new AgentRunnerService(new EventBus(), undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(approveAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
          approveApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, failingWorktreeManager, undefined, approveAgentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, stateManager.getCheckSuiteRepo(), stageExecutionRepo, undefined, stageStateService, workflowRunService));
          const approveServer = createTestServer(approveApp);

          const response = await request(approveServer).post(`/api/issues/${issue.number}/approve`);

          expect(response.status).toBe(409);
          expect(response.body.error).toContain('failed to validate current worktree state');
          expect(enqueueSpy).not.toHaveBeenCalled();
        } finally {
          process.env.HOME = originalHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      });

      it('Direct merge for non-Integrate issue should return bypass error', async () => {
        const issue = issueService.create({ projectId, title: 'Direct Merge Test' });
        issueService.transitionToStage(issue.id, Stage.Check);
        issueService.setStatus(issue.id, IssueStatus.Active);

        const mergeApp = new Hono();
        const mergeEventBus = new EventBus();
        const mergeAgentRunner = new AgentRunnerService(mergeEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
        mergeApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, mergeAgentRunner));
        const mergeServer = createTestServer(mergeApp);

        const response = await request(mergeServer).post(`/api/issues/${issue.number}/merge`);

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('Direct merge is not allowed');
        expect(response.body.error).toContain('check');
      });
    });

    describe('POST /api/issues/:number/skip-to-review', () => {
      it('should transition to review stage and trigger pipeline', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-skip-test-'));

        try {
          const project = await projectService.create({ name: 'SkipTest', path: tmpDir });
          projectService.setCurrent(project);

          const issue = issueService.create({ projectId: project.id, title: 'Skip Issue' });
          const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issue.number}-test`);
          fs.mkdirSync(changeDir, { recursive: true });
          fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [] }));

          const skipApp = new Hono();
          const skipEventBus = new EventBus();
          const skipAgentRunner = new AgentRunnerService(skipEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), undefined, stateManager.getIssueTaskQueueRepo());
          skipApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, skipAgentRunner));
          const skipServer = createTestServer(skipApp);

          const response = await request(skipServer).post(`/api/issues/${issue.number}/skip-to-review`);

          expect(response.status).toBe(202);
          expect(response.body.success).toBe(true);

          const issueRepo = stateManager.getIssueRepo();
          const updated = issueRepo.findById(issue.id);
          expect(updated?.stage).toBe(Stage.Check);
        } finally {
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      });
    });

    describe('POST /api/issues/:number/reopen', () => {
      it('reopens closed issue and does not auto-enqueue resume-pipeline', async () => {
        const issue = issueService.create({ projectId, title: 'Closed Issue' });
        const issueRepo = stateManager.getIssueRepo();
        issueRepo.updateStage(issue.id, Stage.Build);
        issueRepo.updateStatus(issue.id, IssueStatus.Closed);

        const reopenApp = new Hono();
        const reopenEventBus = new EventBus();
        const reopenAgentRunner = new AgentRunnerService(reopenEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
        const enqueueSpy = vi.spyOn(reopenAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
        reopenApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, reopenAgentRunner));
        const reopenServer = createTestServer(reopenApp);

        const response = await request(reopenServer).post(`/api/issues/${issue.number}/reopen`);

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.message).not.toContain('resume-pipeline');

        expect(enqueueSpy).not.toHaveBeenCalled();

        const reopened = issueRepo.findById(issue.id);
        expect(reopened?.status).toBe(IssueStatus.Active);
        expect(reopened?.stage).toBe(Stage.Build);
      });

      it('returns 404 for blocked issue — reopen is only for closed issues', async () => {
        const issue = issueService.create({ projectId, title: 'Blocked Issue' });
        const issueRepo = stateManager.getIssueRepo();
        issueRepo.updateStage(issue.id, Stage.Build);
        issueRepo.updateStatus(issue.id, IssueStatus.Blocked);

        const reopenApp = new Hono();
        const reopenEventBus = new EventBus();
        const reopenAgentRunner = new AgentRunnerService(reopenEventBus, undefined, stateManager.getIssueRepo(), 8);
        reopenApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, reopenAgentRunner));
        const reopenServer = createTestServer(reopenApp);

        const response = await request(reopenServer).post(`/api/issues/${issue.number}/reopen`);

        expect(response.status).toBe(404);
        expect(response.body.error).toContain('not reopenable');
      });
    });

    describe('POST /api/issues/:number/reject', () => {
      it('clears check checkpoint and stale review artifacts before restarting from build', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-reject-check-test-'));

        try {
          const project = await projectService.create({ name: 'RejectCheckTest', path: tmpDir });
          projectService.setCurrent(project);

          const issue = issueService.create({ projectId: project.id, title: 'Reject Check Issue' });
          const issueRepo = stateManager.getIssueRepo();
          const workflowRunService = new WorkflowRunService(db);
          const workflowApplicationService = new WorkflowApplicationService(db);
          workflowRunService.startRun(issue.id, issue.number);
          completePlanToApproval(workflowApplicationService, issue.id);
          completeCheckToApproval(workflowApplicationService, issue.id, 'reject-sha-001');
          issueRepo.setApprovalState(issue.id, {
            stage: Stage.Check,
            status: 'awaiting',
            output: { snapshotSha: 'reject-sha-001', result: 'PASS' },
            requestedAt: new Date().toISOString(),
          });

          const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issue.number}-test`);
          fs.mkdirSync(changeDir, { recursive: true });
          fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [] }));
          fs.writeFileSync(path.join(changeDir, 'review.md'), '# stale review');
          fs.writeFileSync(path.join(changeDir, 'review-self-check.md'), '# stale self check');

          const checkpointRepo = stateManager.getPipelineCheckpointRepo();
          checkpointRepo.upsert(issue.number, 'check', ['review', 'review-self-check'], null);

          const worktreeManager = {
            getPath: vi.fn().mockReturnValue(tmpDir),
          } as any;

          const rejectApp = new Hono();
          const rejectEventBus = new EventBus();
          const rejectAgentRunner = new AgentRunnerService(rejectEventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, stateManager.getProjectRepo(), worktreeManager, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(rejectAgentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });
          rejectApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, worktreeManager, undefined, rejectAgentRunner, undefined, undefined, undefined, undefined, undefined, stateManager.getPipelineCheckpointRepo(), undefined, undefined, stageExecutionRepo, undefined, undefined, workflowRunService));
          const rejectServer = createTestServer(rejectApp);

          const response = await request(rejectServer)
            .post(`/api/issues/${issue.number}/reject`)
            .send({ message: 'rerun review on latest code' });

          expect(response.status).toBe(202);
          expect(response.body.success).toBe(true);
          expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');
          expect(issueRepo.findById(issue.id)?.status).toBe(IssueStatus.Blocked);
          const rejectedRun = workflowRunService.getActiveRunForIssue(issue.id);
          expect(rejectedRun?.status).toBe('failed');
          const rejectedCheckStage = rejectedRun?.stageRuns.find(stage => stage.stage === Stage.Check);
          expect(rejectedCheckStage?.status).toBe('failed');
          expect(rejectedCheckStage?.approvalStatus).toBe('rejected');
          expect(checkpointRepo.get(issue.number, 'check')).toBeNull();
          expect(fs.existsSync(path.join(changeDir, 'review.md'))).toBe(false);
          expect(fs.existsSync(path.join(changeDir, 'review-self-check.md'))).toBe(false);
        } finally {
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      });

    });

    describe('POST /api/issues/:number/comments', () => {
      it('should add a comment to an issue', async () => {
        const issue = issueService.create({ projectId, title: 'Comment Test' });

        const response = await request(server)
          .post(`/api/issues/${issue.number}/comments`)
          .send({ body: 'Test comment' });

        expect(response.status).toBe(201);
        expect(response.body.success).toBe(true);
        expect(response.body.data.body).toBe('Test comment');
        expect(response.body.data.issueId).toBe(issue.id);
      });

      it('should require body', async () => {
        const issue = issueService.create({ projectId, title: 'Comment Test' });

        const response = await request(server)
          .post(`/api/issues/${issue.number}/comments`)
          .send({});

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('body is required');
      });

      it('should return 404 when issue not found', async () => {
        const response = await request(server)
          .post('/api/issues/999/comments')
          .send({ body: 'Test comment' });

        expect(response.status).toBe(404);
      });
    });

    describe('DELETE /api/issues/:number/comments/:commentId', () => {
      it('should delete a comment that belongs to the issue', async () => {
        const issue = issueService.create({ projectId, title: 'Delete Comment Test' });
        const comment = issueService.createComment(issue.id, 'Comment to delete');

        const response = await request(server)
          .delete(`/api/issues/${issue.number}/comments/${comment.id}`);

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.message).toContain(`Deleted comment ${comment.id} from issue #${issue.number}`);

        const comments = issueService.getCommentsByIssue(issue.id);
        expect(comments.find(c => c.id === comment.id)).toBeUndefined();
      });

      it('should return 404 when comment does not exist', async () => {
        const issue = issueService.create({ projectId, title: 'Delete Comment Test' });

        const response = await request(server)
          .delete(`/api/issues/${issue.number}/comments/non-existent-id`);

        expect(response.status).toBe(404);
        expect(response.body.error).toContain('Comment not found');
      });

      it('should return 404 when trying to delete a comment from another issue', async () => {
        const issue1 = issueService.create({ projectId, title: 'Issue 1' });
        const issue2 = issueService.create({ projectId, title: 'Issue 2' });
        const commentOnIssue1 = issueService.createComment(issue1.id, 'Comment on issue 1');

        const response = await request(server)
          .delete(`/api/issues/${issue2.number}/comments/${commentOnIssue1.id}`);

        expect(response.status).toBe(404);
        expect(response.body.error).toContain('Comment not found');

        const commentsOnIssue1 = issueService.getCommentsByIssue(issue1.id);
        expect(commentsOnIssue1.find(c => c.id === commentOnIssue1.id)).toBeDefined();
      });

      it('should return 404 when issue does not exist', async () => {
        const response = await request(server)
          .delete('/api/issues/999/comments/some-id');

        expect(response.status).toBe(404);
        expect(response.body.error).toContain('Issue #999 not found');
      });

      it('should return 400 when no active project', async () => {
        projectService.clearCurrent();

        const response = await request(server)
          .delete('/api/issues/1/comments/some-id');

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('No active project');
      });
    });
  });

  describe('Status Routes', () => {
    let server: http.Server;

    beforeEach(() => {
      const app = new Hono();
      app.route('/api', createStatusRoutes(projectService, issueService));
      server = createTestServer(app);
    });

    describe('GET /api/status', () => {
      it('should return error when no current project', async () => {
        const response = await request(server).get('/api/status');

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('No active project');
      });

      it('should return current project status', async () => {
        const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
        projectService.setCurrent(project);

        const response = await request(server).get('/api/status');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.name).toBe('Test Project');
      });

      it('should return llm.configured false when no llmConfig provided', async () => {
        const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
        projectService.setCurrent(project);

        const response = await request(server).get('/api/status');

        expect(response.status).toBe(200);
        expect(response.body.data.llm).toBeDefined();
        expect(response.body.data.llm.configured).toBe(false);
        expect(response.body.data.llm.provider).toBeUndefined();
        expect(response.body.data.llm.model).toBeUndefined();
      });

      it('should not expose apiKey in llm status', async () => {
        const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
        projectService.setCurrent(project);

        const response = await request(server).get('/api/status');

        expect(response.status).toBe(200);
        const llmJson = JSON.stringify(response.body.data.llm);
        expect(llmJson).not.toContain('apiKey');
      });

      it('should return llm.configured false when llmConfig has no apiKey', async () => {
        const noKeyApp = new Hono();
        noKeyApp.route('/api', createStatusRoutes(projectService, issueService, { model: 'anthropic/claude-sonnet-4-20250514' }));
        const noKeyServer = createTestServer(noKeyApp);

        const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
        projectService.setCurrent(project);

        const response = await request(noKeyServer).get('/api/status');

        expect(response.status).toBe(200);
        expect(response.body.data.llm.configured).toBe(false);
      });
    });

    describe('GET /api/status?all=true', () => {
      it('should return all projects status', async () => {
        await projectService.create({ name: 'Project 1', path: '/path/1' });
        await projectService.create({ name: 'Project 2', path: '/path/2' });

        const response = await request(server).get('/api/status?all=true');

        expect(response.status).toBe(200);
        expect(response.body.data).toHaveLength(2);
      });
    });
  });

  describe('Config Routes', () => {
    let server: http.Server;

    beforeEach(() => {
      const app = new Hono();
      app.route('/api/config', createConfigRoutes(configService));
      server = createTestServer(app);
    });

    describe('GET /api/config', () => {
      it('should return config', async () => {
        const response = await request(server).get('/api/config');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.agentTimeout).toBeDefined();
      });
    });

    describe('PUT /api/config/:key', () => {
      it('should update config value', async () => {
        const response = await request(server)
          .put('/api/config/agent.timeout')
          .send({ value: 2000000 });

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
      });

      it('should validate agent timeout minimum', async () => {
        const response = await request(server)
          .put('/api/config/agent.timeout')
          .send({ value: 1000 });

        expect(response.status).toBe(400);
      });
    });

    describe('GET /api/config/list', () => {
      it('should return all config values', async () => {
        const response = await request(server).get('/api/config/list');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data).toBeDefined();
      });
    });
  });

  describe('Issue Retry/Restart Routes', () => {
    let server: http.Server;
    let projectId: string;

    function createBlockedIssue(title: string) {
      const issue = issueService.create({ projectId, title });
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateStage(issue.id, Stage.Build);
      issueRepo.blockIssue(issue.id, `Build 中断 — ${title}`);
      issueRepo.updateRetryCount(issue.id, 3);
      return issue;
    }

    function createRetryServer(agentRunner: AgentRunnerService) {
      const app = new Hono();
      const prerequisiteService = new IssuePrerequisiteService(issueRepo, stateManager.getIssueStartPrerequisiteRepo());
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, agentRunner, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, prerequisiteService));
      return createTestServer(app);
    }

    beforeEach(async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectId = project.id;
      projectService.setCurrent(project);
    });

    describe('POST /api/issues/:number/retry', () => {
      it('should reject retry for merged blocked issue and require manual intervention', async () => {
        const issue = createBlockedIssue('Merged Blocked');
        issueRepo.updateStage(issue.id, Stage.Done);
        issueRepo.update(issue.id, { mergeState: MergeState.Merged });
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/retry`);

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('manual intervention');

        const updated = stateManager.getIssueRepo().findById(issue.id);
        expect(updated?.stage).toBe(Stage.Done);
        expect(updated?.status).toBe(IssueStatus.Blocked);
      });

      it('should reject retry for integrate-stage blocked issue and require manual intervention', async () => {
        const issue = createBlockedIssue('Integrate Blocked');
        issueRepo.updateStage(issue.id, Stage.Integrate);
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/retry`);

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('manual intervention');

        const updated = stateManager.getIssueRepo().findById(issue.id);
        expect(updated?.stage).toBe(Stage.Integrate);
        expect(updated?.status).toBe(IssueStatus.Blocked);
      });

      it('should reject retry when no checkpoint found', async () => {
        const issue = createBlockedIssue('Retry Test');
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/retry`);

        expect(response.status).toBe(409);
        expect(response.body.success).toBe(false);
        expect(response.body.error).toContain('checkpoint');

        const issueRepo = stateManager.getIssueRepo();
        const updated = issueRepo.findById(issue.id);
        expect(updated?.status).toBe(IssueStatus.Blocked);
        expect(updated?.stage).not.toBe(Stage.Backlog);
      });

      it('should retry from checkpoint when worktree has tasks.json', async () => {
        const tmpRetryDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-retry-test-'));

        try {
          const retryProject = await projectService.create({ name: 'RetryCheckpoint', path: tmpRetryDir });
          projectService.setCurrent(retryProject);

          const issue = issueService.create({ projectId: retryProject.id, title: 'Retry Checkpoint' });
          const issueRepo = stateManager.getIssueRepo();
          issueRepo.updateStage(issue.id, Stage.Build);
          issueRepo.blockIssue(issue.id, 'Build interrupted');
          issueRepo.updateRetryCount(issue.id, 2);

          const changeDir = path.join(tmpRetryDir, 'openspec', 'changes', `${issue.number}-test-change`);
          fs.mkdirSync(changeDir, { recursive: true });
          fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [{ id: 'T-001', passes: true }] }));

          const eventBus = new EventBus();
          const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });

          const mockWm = {
            getPath: () => tmpRetryDir,
            exists: () => true,
          } as any;

          const retryApp = new Hono();
          retryApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, mockWm, undefined, agentRunner, undefined, undefined, undefined, undefined, undefined, stateManager.getPipelineCheckpointRepo()));
          const retryServer = createTestServer(retryApp);

          const response = await request(retryServer).post(`/api/issues/${issue.number}/retry`);

          expect(response.status).toBe(202);
          expect(response.body.success).toBe(true);
          expect(response.body.data.message).toContain('retrying from checkpoint');

          const updated = issueRepo.findById(issue.id);
          expect(updated?.status).toBe(IssueStatus.Active);
          expect(updated?.blockedReason).toBeUndefined();
          expect(updated?.retryCount).toBe(0);

          expect(enqueueSpy).toHaveBeenCalledTimes(1);
          expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');
        } finally {
          fs.rmSync(tmpRetryDir, { recursive: true, force: true });
        }
      });

      it('does not activate or enqueue retry when workflow retry mutation rejects after availability check', async () => {
        const tmpRetryDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-retry-reject-test-'));

        try {
          const retryProject = await projectService.create({ name: 'RetryRejects', path: tmpRetryDir });
          projectService.setCurrent(retryProject);

          const issue = issueService.create({ projectId: retryProject.id, title: 'Retry Rejects Interrupted' });
          issueRepo.updateStage(issue.id, Stage.Build);
          issueRepo.blockIssue(issue.id, 'Build interrupted');
          issueRepo.updateRetryCount(issue.id, 2);

          const workflowRunService = new WorkflowRunService(db);
          const workflowApplicationService = new WorkflowApplicationService(db);
          workflowApplicationService.startWorkflow({ issueId: issue.id, issueNumber: issue.number });
          completePlanToApproval(workflowApplicationService, issue.id);
          workflowApplicationService.approveStage({ issueId: issue.id, stage: Stage.Plan, approval: { output: { approved: true } } });
          workflowApplicationService.materializeTasks({ issueId: issue.id, stage: Stage.Build, tasks: [{ id: 'T-001', title: 'Build task', order: 1 }] });
          workflowApplicationService.startTaskAttempt({ issueId: issue.id, stage: Stage.Build, taskId: 'T-001', evidence: { executionId: 'build-failed' } });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Build, taskId: 'T-001', result: { status: 'failed', error: 'failed before retry' } });

          const changeDir = path.join(tmpRetryDir, 'openspec', 'changes', `${issue.number}-test-change`);
          fs.mkdirSync(changeDir, { recursive: true });
          fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [{ id: 'T-001', passes: true }] }));

          const eventBus = new EventBus();
          const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });

          const mockWm = {
            getPath: () => tmpRetryDir,
            exists: () => true,
          } as any;

          const retryApp = new Hono();
          retryApp.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, mockWm, undefined, agentRunner, undefined, undefined, undefined, undefined, undefined, stateManager.getPipelineCheckpointRepo(), undefined, undefined, undefined, undefined, undefined, workflowRunService));
          const retryServer = createTestServer(retryApp);
          const retrySpy = vi.spyOn(WorkflowApplicationService.prototype, 'retryStageOrReject').mockReturnValue({
            ok: false,
            reason: 'no-retryable-failed-work',
            message: 'Retry rejected after availability check',
          });

          try {
            const response = await request(retryServer).post(`/api/issues/${issue.number}/retry`);

            expect(response.status).toBe(409);
            expect(response.body.error).toContain('Retry rejected after availability check');
            expect(enqueueSpy).not.toHaveBeenCalled();
          } finally {
            retrySpy.mockRestore();
          }

          const updated = issueRepo.findById(issue.id);
          expect(updated?.status).toBe(IssueStatus.Blocked);
          expect(updated?.retryCount).toBe(2);
        } finally {
          fs.rmSync(tmpRetryDir, { recursive: true, force: true });
        }
      });

      it('should return 409 when issue is not blocked', async () => {
        const issue = await issueService.create({ projectId, title: 'Active Issue' });
        issueRepo.updateStage(issue.id, Stage.Plan);
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).post('/api/issues/1/retry');

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('not blocked');
      });

      it('should reject retry when issue has a running slot and no checkpoint', async () => {
        const issue = createBlockedIssue('Running Agent');
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
        (agentRunner as any).runningSlots.set(issue.id, { id: 'fake-task', issueId: issue.id });
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/retry`);

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('checkpoint');
      });

      it('should return 404 when issue not found', async () => {
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).post('/api/issues/999/retry');

        expect(response.status).toBe(404);
      });

      it('should reject retry and keep stage when no checkpoint found', async () => {
        const issue = createBlockedIssue('No Checkpoint');
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/retry`);

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('checkpoint');

        const issueRepo = stateManager.getIssueRepo();
        const updated = issueRepo.findById(issue.id);
        expect(updated?.stage).not.toBe(Stage.Backlog);
        expect(updated?.status).toBe(IssueStatus.Blocked);
      });
    });

    describe('POST /api/issues/:number/check/retry-checkpoint', () => {
      it('retries the Check checkpoint and resumes the pipeline when repair budget is exhausted', async () => {
        const tmpRetryDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-check-retry-test-'));

        try {
          const retryProject = await projectService.create({ name: 'CheckRetryCheckpoint', path: tmpRetryDir });
          projectService.setCurrent(retryProject);

          const issue = issueService.create({ projectId: retryProject.id, title: 'Retry Check Checkpoint' });
          issueRepo.updateStage(issue.id, Stage.Check);
          issueRepo.blockIssue(issue.id, 'Review still failing');
          issueRepo.updateRetryCount(issue.id, 2);

          const workflowRunService = new WorkflowRunService(db, issueRepo);
          const workflowApplicationService = new WorkflowApplicationService(db);
          const stageStateService = new StageStateService(db);

          workflowApplicationService.startWorkflow({ issueId: issue.id, issueNumber: issue.number });
          completePlanToApproval(workflowApplicationService, issue.id);
          workflowApplicationService.approveStage({ issueId: issue.id, stage: Stage.Plan, approval: { output: { approved: true } } });
          workflowApplicationService.materializeTasks({ issueId: issue.id, stage: Stage.Build, tasks: [{ id: 'T-001', title: 'Build task', order: 1 }] });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Build, taskId: 'T-001', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Build, result: { name: 'health:build', status: 'pass' } });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Check, taskId: 'ai-review', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'health:check', status: 'pass' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'review-passed', status: 'fail', message: 'Initial review failure', output: { verdict: 'FAIL', summary: 'Initial review failure' } } });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Check, taskId: 'fix-review-findings', result: { status: 'completed' } });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Check, taskId: 'ai-review', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'health:check', status: 'pass' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'review-passed', status: 'fail', message: 'Still failing after repair', output: { verdict: 'FAIL', summary: 'Still failing after repair' } } });

          const changeDir = path.join(tmpRetryDir, 'openspec', 'changes', `${issue.number}-test-change`);
          fs.mkdirSync(changeDir, { recursive: true });
          fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [{ id: 'T-001', passes: true }] }));

          const eventBus = new EventBus();
          const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake-retry-check', status: 'pending' });

          const mockWm = {
            getPath: () => tmpRetryDir,
            exists: () => true,
          } as any;

          const retryApp = new Hono();
          retryApp.route('/api/issues', createIssueRoutes(
            issueService,
            projectService,
            stateManager,
            mockWm,
            undefined,
            agentRunner,
            undefined,
            undefined,
            undefined,
            undefined,
            undefined,
            stateManager.getPipelineCheckpointRepo(),
            undefined,
            undefined,
            undefined,
            undefined,
            stageStateService,
            workflowRunService,
          ));
          const retryServer = createTestServer(retryApp);

          const response = await request(retryServer).post(`/api/issues/${issue.number}/check/retry-checkpoint`);

          expect(response.status).toBe(202);
          expect(response.body.success).toBe(true);
          expect(response.body.data.message).toContain('repair budget is exhausted');
          expect(response.body.data.repairBudgetExhausted).toBe(true);
          expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');

          const updated = issueRepo.findById(issue.id);
          expect(updated?.status).toBe(IssueStatus.Active);
          expect(updated?.blockedReason).toBeUndefined();
          expect(updated?.retryCount).toBe(0);

          const activeRun = workflowRunService.getActiveRunForIssue(issue.id);
          expect(activeRun?.currentStage).toBe(Stage.Check);
          expect(activeRun?.status).toBe('running');
          expect(activeRun?.stageRuns.find(stageRun => stageRun.stage === Stage.Check)?.status).toBe('running');
        } finally {
          fs.rmSync(tmpRetryDir, { recursive: true, force: true });
        }
      });
    });

    describe('POST /api/issues/:number/check/rerun-review', () => {
      it('reruns review without appending a repair task', async () => {
        const tmpRerunDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-check-rerun-review-test-'));

        try {
          const rerunProject = await projectService.create({ name: 'CheckRerunReview', path: tmpRerunDir });
          projectService.setCurrent(rerunProject);

          const issue = issueService.create({ projectId: rerunProject.id, title: 'Rerun Review Only' });
          issueRepo.updateStage(issue.id, Stage.Check);
          issueRepo.blockIssue(issue.id, 'Review still failing');
          const runningSession = stateManager.getCoderSessionRepo().insert({
            issueId: issue.id,
            acpSessionId: 'acp-rerun-review',
            executionId: 'check-review-running',
            stage: Stage.Check,
            title: 'Review still running',
          });

          const workflowRunService = new WorkflowRunService(db, issueRepo);
          const workflowApplicationService = new WorkflowApplicationService(db);
          const stageStateService = new StageStateService(db);

          workflowApplicationService.startWorkflow({ issueId: issue.id, issueNumber: issue.number });
          completePlanToApproval(workflowApplicationService, issue.id);
          workflowApplicationService.approveStage({ issueId: issue.id, stage: Stage.Plan, approval: { output: { approved: true } } });
          workflowApplicationService.materializeTasks({ issueId: issue.id, stage: Stage.Build, tasks: [{ id: 'T-001', title: 'Build task', order: 1 }] });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Build, taskId: 'T-001', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Build, result: { name: 'health:build', status: 'pass' } });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Check, taskId: 'ai-review', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'health:check', status: 'pass' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'review-passed', status: 'fail', message: 'Initial review failure', output: { verdict: 'FAIL', summary: 'Initial review failure' } } });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Check, taskId: 'fix-review-findings', result: { status: 'completed' } });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Check, taskId: 'ai-review', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'health:check', status: 'pass' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'review-passed', status: 'fail', message: 'Still failing after repair', output: { verdict: 'FAIL', summary: 'Still failing after repair' } } });

          const changeDir = path.join(tmpRerunDir, 'openspec', 'changes', `${issue.number}-test-change`);
          fs.mkdirSync(changeDir, { recursive: true });
          fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [{ id: 'T-001', passes: true }] }));
          fs.writeFileSync(path.join(changeDir, 'review.md'), 'stale review');

          const eventBus = new EventBus();
          const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake-rerun-review', status: 'pending' });

          const mockWm = { getPath: () => tmpRerunDir, exists: () => true } as any;
          const rerunApp = new Hono();
          rerunApp.route('/api/issues', createIssueRoutes(
            issueService, projectService, stateManager, mockWm, undefined, agentRunner,
            undefined, undefined, stateManager.getCoderSessionRepo(), undefined, undefined, stateManager.getPipelineCheckpointRepo(),
            undefined, undefined, undefined, undefined, stageStateService, workflowRunService,
          ));
          const rerunServer = createTestServer(rerunApp);

          const response = await request(rerunServer).post(`/api/issues/${issue.number}/check/rerun-review`);

          expect(response.status).toBe(202);
          expect(response.body.success).toBe(true);
          expect(response.body.data.message).toContain('no repair task will be added');
          expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');
          expect(fs.existsSync(path.join(changeDir, 'review.md'))).toBe(false);
          expect(stateManager.getCoderSessionRepo().findById(runningSession.id)).toMatchObject({
            status: 'cancelled',
            failureReason: 'Review rerun requested',
          });

          const activeRun = workflowRunService.getActiveRunForIssue(issue.id);
          const checkRun = activeRun?.stageRuns.find(stageRun => stageRun.stage === Stage.Check);
          const fixTasks = checkRun?.tasks.filter(task => task.taskId.startsWith('fix-review-findings')) ?? [];
          expect(fixTasks).toHaveLength(1);
          expect(fixTasks[0].status).toBe('completed');
          expect(checkRun?.tasks.find(task => task.taskId === 'ai-review')?.status).toBe('pending');
          expect(checkRun?.checks.find(check => check.checkName === 'review-passed')?.status).toBe('pending');
        } finally {
          fs.rmSync(tmpRerunDir, { recursive: true, force: true });
        }
      });

      it('reruns review from awaiting Check approval when retry rejects a running WorkflowRun', async () => {
        const tmpRerunDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-check-rerun-approval-test-'));

        try {
          const rerunProject = await projectService.create({ name: 'CheckRerunApproval', path: tmpRerunDir });
          projectService.setCurrent(rerunProject);

          const issue = issueService.create({ projectId: rerunProject.id, title: 'Rerun Stale Approval Review' });
          issueRepo.updateStage(issue.id, Stage.Check);

          const workflowRunService = new WorkflowRunService(db, issueRepo);
          const workflowApplicationService = new WorkflowApplicationService(db);
          const stageStateService = new StageStateService(db);

          workflowApplicationService.startWorkflow({ issueId: issue.id, issueNumber: issue.number });
          completePlanToApproval(workflowApplicationService, issue.id);
          workflowApplicationService.approveStage({ issueId: issue.id, stage: Stage.Plan, approval: { output: { approved: true } } });
          workflowApplicationService.materializeTasks({ issueId: issue.id, stage: Stage.Build, tasks: [{ id: 'T-001', title: 'Build task', order: 1 }] });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Build, taskId: 'T-001', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Build, result: { name: 'health:build', status: 'pass' } });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Check, taskId: 'ai-review', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'health:check', status: 'pass', output: { candidateHeadSha: 'new-head' } } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'review-passed', status: 'pass', output: { verdict: 'PASS', snapshotSha: 'old-head' } } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'merge-ready', status: 'pass', output: {
            kind: 'merge-ready',
            targetBranch: 'master',
            strategy: 'squash',
            baseSha: 'base',
            candidateHeadSha: 'new-head',
            mergeBaseSha: 'base',
            canMerge: true,
            conflictFiles: [],
            checkedAt: new Date().toISOString(),
          } } });

          const changeDir = path.join(tmpRerunDir, 'openspec', 'changes', `${issue.number}-test-change`);
          fs.mkdirSync(changeDir, { recursive: true });
          fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [{ id: 'T-001', passes: true }] }));
          fs.writeFileSync(path.join(changeDir, 'review.md'), 'stale review');

          const eventBus = new EventBus();
          const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake-rerun-review', status: 'pending' });

          const mockWm = { getPath: () => tmpRerunDir, exists: () => true } as any;
          const rerunApp = new Hono();
          rerunApp.route('/api/issues', createIssueRoutes(
            issueService, projectService, stateManager, mockWm, undefined, agentRunner,
            undefined, undefined, undefined, undefined, undefined, stateManager.getPipelineCheckpointRepo(),
            undefined, undefined, undefined, undefined, stageStateService, workflowRunService,
          ));
          const rerunServer = createTestServer(rerunApp);

          const response = await request(rerunServer).post(`/api/issues/${issue.number}/check/rerun-review`);

          expect(response.status).toBe(202);
          expect(response.body.success).toBe(true);
          expect(enqueueSpy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');
          expect(fs.existsSync(path.join(changeDir, 'review.md'))).toBe(false);

          const activeRun = workflowRunService.getActiveRunForIssue(issue.id);
          const checkRun = activeRun?.stageRuns.find(stageRun => stageRun.stage === Stage.Check);
          expect(checkRun?.status).toBe('running');
          expect(checkRun?.approvalStatus).toBeNull();
          expect(checkRun?.tasks.find(task => task.taskId === 'ai-review')?.status).toBe('pending');
          expect(checkRun?.checks.find(check => check.checkName === 'review-passed')?.status).toBe('pending');
        } finally {
          fs.rmSync(tmpRerunDir, { recursive: true, force: true });
        }
      });

      it('cancels running coder sessions when rerunning a stage', async () => {
        const rerunProject = await projectService.create({ name: 'StageRerunCancels', path: fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-stage-rerun-cancels-')) });
        projectService.setCurrent(rerunProject);

        const issue = issueService.create({ projectId: rerunProject.id, title: 'Rerun Cancels Session' });
        issueRepo.updateStage(issue.id, Stage.Build);
        issueRepo.blockIssue(issue.id, 'Build stuck');
        const runningSession = stateManager.getCoderSessionRepo().insert({
          issueId: issue.id,
          acpSessionId: 'acp-rerun-stage',
          executionId: 'build-running',
          stage: Stage.Build,
          title: 'Build still running',
        });

        const workflowRunService = new WorkflowRunService(db, issueRepo);
        const workflowApplicationService = new WorkflowApplicationService(db);
        workflowApplicationService.startWorkflow({ issueId: issue.id, issueNumber: issue.number });
        completePlanToApproval(workflowApplicationService, issue.id);
        workflowApplicationService.approveStage({ issueId: issue.id, stage: Stage.Plan, approval: { output: { approved: true } } });
        workflowApplicationService.materializeTasks({ issueId: issue.id, stage: Stage.Build, tasks: [{ id: 'T-001', title: 'Build task', order: 1 }] });
        workflowApplicationService.startTaskAttempt({ issueId: issue.id, stage: Stage.Build, taskId: 'T-001', evidence: { executionId: 'build-running' } });

        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
        vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake-rerun-stage', status: 'pending' });

        const rerunApp = new Hono();
        rerunApp.route('/api/issues', createIssueRoutes(
          issueService, projectService, stateManager, undefined, undefined, agentRunner,
          undefined, undefined, stateManager.getCoderSessionRepo(), undefined, undefined, undefined,
          undefined, undefined, undefined, undefined, undefined, workflowRunService,
        ));
        const rerunServer = createTestServer(rerunApp);

        const response = await request(rerunServer).post(`/api/issues/${issue.number}/rerun`);

        expect(response.status).toBe(202);
        expect(stateManager.getCoderSessionRepo().findById(runningSession.id)).toMatchObject({
          status: 'cancelled',
          failureReason: 'Stage rerun requested',
        });
      });

      it('rejects rerun when the reconciled latest attempt is still running', async () => {
        const rerunProject = await projectService.create({ name: 'StageRerunBlockedByRunningAttempt', path: fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-stage-rerun-running-')) });
        projectService.setCurrent(rerunProject);

        const issue = issueService.create({ projectId: rerunProject.id, title: 'Rerun blocked by running attempt' });
        issueRepo.updateStage(issue.id, Stage.Build);
        issueRepo.blockIssue(issue.id, 'Build appears stuck');
        const runningSession = stateManager.getCoderSessionRepo().insert({
          issueId: issue.id,
          acpSessionId: 'acp-rerun-running',
          executionId: 'build-running-live',
          stage: Stage.Build,
          title: 'Build still running',
          processPid: process.pid,
        });

        const workflowRunService = new WorkflowRunService(db, issueRepo);
        const workflowApplicationService = new WorkflowApplicationService(db);
        workflowApplicationService.startWorkflow({ issueId: issue.id, issueNumber: issue.number });
        completePlanToApproval(workflowApplicationService, issue.id);
        workflowApplicationService.approveStage({ issueId: issue.id, stage: Stage.Plan, approval: { output: { approved: true } } });
        workflowApplicationService.materializeTasks({ issueId: issue.id, stage: Stage.Build, tasks: [{ id: 'T-001', title: 'Build task', order: 1 }] });
        workflowApplicationService.startTaskAttempt({
          issueId: issue.id,
          stage: Stage.Build,
          taskId: 'T-001',
          evidence: { executionId: 'build-running-live', processPid: process.pid },
        });

        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
        const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake-rerun-running', status: 'pending' });

        const rerunApp = new Hono();
        rerunApp.route('/api/issues', createIssueRoutes(
          issueService, projectService, stateManager, undefined, undefined, agentRunner,
          undefined, undefined, stateManager.getCoderSessionRepo(), undefined, undefined, undefined,
          undefined, undefined, undefined, undefined, undefined, workflowRunService,
        ));
        const rerunServer = createTestServer(rerunApp);

        const response = await request(rerunServer).post(`/api/issues/${issue.number}/rerun`);

        expect(response.status).toBe(409);
        expect(response.body.success).toBe(false);
        expect(response.body.error).toContain('Cannot rerun');
        expect(response.body.error).toContain('is running');
        expect(response.body.error).toContain('stop it first');
        expect(enqueueSpy).not.toHaveBeenCalled();
        expect(stateManager.getCoderSessionRepo().findById(runningSession.id)).toMatchObject({
          status: 'running',
        });

        const updated = issueRepo.findById(issue.id);
        expect(updated?.stage).toBe(Stage.Build);
        expect(updated?.status).toBe(IssueStatus.Active);
      });
    });

    describe('POST /api/issues/:number/check/repair-review-findings', () => {
      it('reuses the WorkflowRun-scheduled repair without enqueueing duplicate resume work', async () => {
        const tmpRepairDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-check-repair-test-'));

        try {
          const repairProject = await projectService.create({ name: 'CheckRepairFindings', path: tmpRepairDir });
          projectService.setCurrent(repairProject);

          const issue = issueService.create({ projectId: repairProject.id, title: 'Repair Check Findings' });
          issueRepo.updateStage(issue.id, Stage.Check);
          issueRepo.blockIssue(issue.id, 'Review failed');
          issueRepo.updateRetryCount(issue.id, 2);

          const workflowRunService = new WorkflowRunService(db, issueRepo);
          const workflowApplicationService = new WorkflowApplicationService(db);
          const stageStateService = new StageStateService(db);

          workflowApplicationService.startWorkflow({ issueId: issue.id, issueNumber: issue.number });
          completePlanToApproval(workflowApplicationService, issue.id);
          workflowApplicationService.approveStage({ issueId: issue.id, stage: Stage.Plan, approval: { output: { approved: true } } });
          workflowApplicationService.materializeTasks({ issueId: issue.id, stage: Stage.Build, tasks: [{ id: 'T-001', title: 'Build task', order: 1 }] });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Build, taskId: 'T-001', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Build, result: { name: 'health:build', status: 'pass' } });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Check, taskId: 'ai-review', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'health:check', status: 'pass' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'review-passed', status: 'fail', message: 'Initial review failure', output: { verdict: 'FAIL', summary: 'Initial review failure' } } });

          const changeDir = path.join(tmpRepairDir, 'openspec', 'changes', `${issue.number}-test-change`);
          fs.mkdirSync(changeDir, { recursive: true });
          fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [{ id: 'T-001', passes: true }] }));

          const eventBus = new EventBus();
          const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake-repair-check', status: 'pending' });

          const mockWm = {
            getPath: () => tmpRepairDir,
            exists: () => true,
          } as any;

          const repairApp = new Hono();
          repairApp.route('/api/issues', createIssueRoutes(
            issueService,
            projectService,
            stateManager,
            mockWm,
            undefined,
            agentRunner,
            undefined,
            undefined,
            undefined,
            undefined,
            undefined,
            stateManager.getPipelineCheckpointRepo(),
            undefined,
            undefined,
            undefined,
            undefined,
            stageStateService,
            workflowRunService,
          ));
          const repairServer = createTestServer(repairApp);

          const response = await request(repairServer).post(`/api/issues/${issue.number}/check/repair-review-findings`);

          expect(response.status).toBe(200);
          expect(response.body.success).toBe(true);
          expect(response.body.data.repairTaskId).toBe('fix-review-findings');
          expect(response.body.data.message).toContain('already in progress');
          expect(response.body.data.taskId).toBeUndefined();
          expect(enqueueSpy).not.toHaveBeenCalled();

          const updated = issueRepo.findById(issue.id);
          expect(updated?.status).toBe(IssueStatus.Active);
          expect(updated?.blockedReason).toBeUndefined();
          expect(updated?.retryCount).toBe(0);

          const activeRun = workflowRunService.getActiveRunForIssue(issue.id);
          const checkRun = activeRun?.stageRuns.find(stageRun => stageRun.stage === Stage.Check);
          expect(activeRun?.status).toBe('running');
          expect(checkRun?.status).toBe('running');
          expect(checkRun?.tasks.some(task => task.taskId === 'fix-review-findings' && task.status === 'pending')).toBe(true);
        } finally {
          fs.rmSync(tmpRepairDir, { recursive: true, force: true });
        }
      });

      it('reuses a pending repair task idempotently without enqueueing duplicate resume work', async () => {
        const tmpRepairDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-check-repair-idempotent-test-'));

        try {
          const repairProject = await projectService.create({ name: 'CheckRepairIdempotent', path: tmpRepairDir });
          projectService.setCurrent(repairProject);

          const issue = issueService.create({ projectId: repairProject.id, title: 'Repair Check Idempotent' });
          issueRepo.updateStage(issue.id, Stage.Check);
          issueRepo.blockIssue(issue.id, 'Review failed');

          const workflowRunService = new WorkflowRunService(db, issueRepo);
          const workflowApplicationService = new WorkflowApplicationService(db);
          const stageStateService = new StageStateService(db);

          workflowApplicationService.startWorkflow({ issueId: issue.id, issueNumber: issue.number });
          completePlanToApproval(workflowApplicationService, issue.id);
          workflowApplicationService.approveStage({ issueId: issue.id, stage: Stage.Plan, approval: { output: { approved: true } } });
          workflowApplicationService.materializeTasks({ issueId: issue.id, stage: Stage.Build, tasks: [{ id: 'T-001', title: 'Build task', order: 1 }] });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Build, taskId: 'T-001', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Build, result: { name: 'health:build', status: 'pass' } });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Check, taskId: 'ai-review', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'health:check', status: 'pass' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'review-passed', status: 'fail', message: 'Initial review failure', output: { verdict: 'FAIL', summary: 'Initial review failure' } } });

          const changeDir = path.join(tmpRepairDir, 'openspec', 'changes', `${issue.number}-test-change`);
          fs.mkdirSync(changeDir, { recursive: true });
          fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [{ id: 'T-001', passes: true }] }));

          const eventBus = new EventBus();
          const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake-repair-idempotent', status: 'pending' });

          const mockWm = { getPath: () => tmpRepairDir, exists: () => true } as any;
          const repairApp = new Hono();
          repairApp.route('/api/issues', createIssueRoutes(
            issueService, projectService, stateManager, mockWm, undefined, agentRunner,
            undefined, undefined, undefined, undefined, undefined, stateManager.getPipelineCheckpointRepo(),
            undefined, undefined, undefined, undefined, stageStateService, workflowRunService,
          ));
          const repairServer = createTestServer(repairApp);

          const response = await request(repairServer).post(`/api/issues/${issue.number}/check/repair-review-findings`);

          expect(response.status).toBe(200);
          expect(response.body.success).toBe(true);
          expect(response.body.data.repairTaskId).toBe('fix-review-findings');
          expect(response.body.data.message).toContain('already in progress');
          expect(enqueueSpy).not.toHaveBeenCalled();
        } finally {
          fs.rmSync(tmpRepairDir, { recursive: true, force: true });
        }
      });

      it('rejects repair when review has not failed', async () => {
        const tmpRepairDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-check-repair-unavailable-test-'));

        try {
          const repairProject = await projectService.create({ name: 'CheckRepairUnavailable', path: tmpRepairDir });
          projectService.setCurrent(repairProject);

          const issue = issueService.create({ projectId: repairProject.id, title: 'Repair Check Unavailable' });
          issueRepo.updateStage(issue.id, Stage.Check);

          const workflowRunService = new WorkflowRunService(db, issueRepo);
          const workflowApplicationService = new WorkflowApplicationService(db);
          const stageStateService = new StageStateService(db);

          workflowApplicationService.startWorkflow({ issueId: issue.id, issueNumber: issue.number });
          completePlanToApproval(workflowApplicationService, issue.id);
          workflowApplicationService.approveStage({ issueId: issue.id, stage: Stage.Plan, approval: { output: { approved: true } } });
          workflowApplicationService.materializeTasks({ issueId: issue.id, stage: Stage.Build, tasks: [{ id: 'T-001', title: 'Build task', order: 1 }] });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Build, taskId: 'T-001', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Build, result: { name: 'health:build', status: 'pass' } });

          const changeDir = path.join(tmpRepairDir, 'openspec', 'changes', `${issue.number}-test-change`);
          fs.mkdirSync(changeDir, { recursive: true });
          fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [{ id: 'T-001', passes: true }] }));

          const eventBus = new EventBus();
          const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake-repair-unavailable', status: 'pending' });

          const mockWm = { getPath: () => tmpRepairDir, exists: () => true } as any;
          const repairApp = new Hono();
          repairApp.route('/api/issues', createIssueRoutes(
            issueService, projectService, stateManager, mockWm, undefined, agentRunner,
            undefined, undefined, undefined, undefined, undefined, stateManager.getPipelineCheckpointRepo(),
            undefined, undefined, undefined, undefined, stageStateService, workflowRunService,
          ));
          const repairServer = createTestServer(repairApp);

          const response = await request(repairServer).post(`/api/issues/${issue.number}/check/repair-review-findings`);

          expect(response.status).toBe(409);
          expect(response.body.success).toBe(false);
          expect(response.body.error).toContain('only available after the Check review has failed');
          expect(enqueueSpy).not.toHaveBeenCalled();
        } finally {
          fs.rmSync(tmpRepairDir, { recursive: true, force: true });
        }
      });

      it('rejects repair when the repair budget is exhausted', async () => {
        const tmpRepairDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-check-repair-exhausted-test-'));

        try {
          const repairProject = await projectService.create({ name: 'CheckRepairExhausted', path: tmpRepairDir });
          projectService.setCurrent(repairProject);

          const issue = issueService.create({ projectId: repairProject.id, title: 'Repair Check Exhausted' });
          issueRepo.updateStage(issue.id, Stage.Check);
          issueRepo.blockIssue(issue.id, 'Review still failing');

          const workflowRunService = new WorkflowRunService(db, issueRepo);
          const workflowApplicationService = new WorkflowApplicationService(db);
          const stageStateService = new StageStateService(db);

          workflowApplicationService.startWorkflow({ issueId: issue.id, issueNumber: issue.number });
          completePlanToApproval(workflowApplicationService, issue.id);
          workflowApplicationService.approveStage({ issueId: issue.id, stage: Stage.Plan, approval: { output: { approved: true } } });
          workflowApplicationService.materializeTasks({ issueId: issue.id, stage: Stage.Build, tasks: [{ id: 'T-001', title: 'Build task', order: 1 }] });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Build, taskId: 'T-001', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Build, result: { name: 'health:build', status: 'pass' } });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Check, taskId: 'ai-review', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'health:check', status: 'pass' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'review-passed', status: 'fail', message: 'Initial review failure', output: { verdict: 'FAIL', summary: 'Initial review failure' } } });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Check, taskId: 'fix-review-findings', result: { status: 'completed' } });
          workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Check, taskId: 'ai-review', result: { status: 'completed' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'health:check', status: 'pass' } });
          workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Check, result: { name: 'review-passed', status: 'fail', message: 'Still failing after repair', output: { verdict: 'FAIL', summary: 'Still failing after repair' } } });

          const changeDir = path.join(tmpRepairDir, 'openspec', 'changes', `${issue.number}-test-change`);
          fs.mkdirSync(changeDir, { recursive: true });
          fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [{ id: 'T-001', passes: true }] }));

          const eventBus = new EventBus();
          const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());
          const enqueueSpy = vi.spyOn(agentRunner, 'enqueue').mockReturnValue({ taskId: 'fake-repair-exhausted', status: 'pending' });

          const mockWm = { getPath: () => tmpRepairDir, exists: () => true } as any;
          const repairApp = new Hono();
          repairApp.route('/api/issues', createIssueRoutes(
            issueService, projectService, stateManager, mockWm, undefined, agentRunner,
            undefined, undefined, undefined, undefined, undefined, stateManager.getPipelineCheckpointRepo(),
            undefined, undefined, undefined, undefined, stageStateService, workflowRunService,
          ));
          const repairServer = createTestServer(repairApp);

          const response = await request(repairServer).post(`/api/issues/${issue.number}/check/repair-review-findings`);

          expect(response.status).toBe(409);
          expect(response.body.success).toBe(false);
          expect(response.body.error).toContain('Repair budget exhausted');
          expect(enqueueSpy).not.toHaveBeenCalled();
        } finally {
          fs.rmSync(tmpRepairDir, { recursive: true, force: true });
        }
      });
    });

    describe('POST /api/issues/:number/restart', () => {
      it('returns deprecation error for any issue — restart has been removed', async () => {
        const issue = createBlockedIssue('Merged Restart');
        issueRepo.updateStage(issue.id, Stage.Done);
        issueRepo.update(issue.id, { mergeState: MergeState.Merged });
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/restart`);

        expect(response.status).toBe(410);
        expect(response.body.success).toBe(false);
        expect(response.body.error).toContain('restart has been removed');
        expect(response.body.error).toContain('retry');
        expect(response.body.error).toContain('rerun');
      });

      it('returns deprecation error for integrate-stage issue', async () => {
        const issue = createBlockedIssue('Integrate Restart');
        issueRepo.updateStage(issue.id, Stage.Integrate);
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/restart`);

        expect(response.status).toBe(410);
        expect(response.body.error).toContain('restart has been removed');

        const updated = stateManager.getIssueRepo().findById(issue.id);
        expect(updated?.stage).toBe(Stage.Integrate);
        expect(updated?.status).toBe(IssueStatus.Blocked);
      });

      it('returns deprecation error without mutating stage', async () => {
        const issue = createBlockedIssue('Restart Test');
        issueRepo.updateStage(issue.id, Stage.Build);
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/restart`);

        expect(response.status).toBe(410);
        expect(response.body.success).toBe(false);
        expect(response.body.error).toContain('restart has been removed');

        const updated = stateManager.getIssueRepo().findById(issue.id);
        expect(updated?.stage).toBe(Stage.Build);
        expect(updated?.status).toBe(IssueStatus.Blocked);
      });

      it('returns 404 when issue not found', async () => {
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).post('/api/issues/999/restart');

        expect(response.status).toBe(404);
      });
    });

    describe('POST /api/issues/:number/start rejects blocked', () => {
      it('should return 400 when trying to start a blocked issue', async () => {
        const issue = createBlockedIssue('Blocked Start');
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).post(`/api/issues/${issue.number}/start`);

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('blocked');
        expect(response.body.error).toContain('retry');
      });
    });

    describe('GET /api/issues/:number returns blockedReason', () => {
      it('should return blockedReason for blocked issue', async () => {
        const issue = createBlockedIssue('Show Reason');
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).get(`/api/issues/${issue.number}`);

        expect(response.status).toBe(200);
        expect(response.body.data.blockedReason).toContain('Build 中断');
        expect(response.body.data.retryCount).toBe(3);
      });

      it('should return undefined blockedReason for non-blocked issue', async () => {
        await issueService.create({ projectId, title: 'Normal Issue' });
        const eventBus = new EventBus();
        const agentRunner = new AgentRunnerService(eventBus);
        server = createRetryServer(agentRunner);

        const response = await request(server).get('/api/issues/1');

        expect(response.status).toBe(200);
        expect(response.body.data.blockedReason).toBeUndefined();
      });
    });
  });

  describe('Agent Status Routes', () => {
    let server: http.Server;

    beforeEach(async () => {
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, stateManager.getIssueRepo(), 8);
      const { createAgentRoutes } = await import('../src/api/agent');
      app.route('/api/agent', createAgentRoutes(agentRunner));
      server = createTestServer(app);
    });

    it('should return blockedIssues array in agent status', async () => {
      const response = await request(server).get('/api/agent/status');

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.blockedIssues).toBeDefined();
      expect(Array.isArray(response.body.data.blockedIssues)).toBe(true);
    });

    it('should return blocked issues with reason and retryCount', async () => {
      const project = await projectService.create({ name: 'AgentTest', path: '/test/path' });
      const issue = issueService.create({ projectId: project.id, title: 'Blocked' });
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.blockIssue(issue.id, 'Test blocked reason');
      issueRepo.updateRetryCount(issue.id, 2);

      const response = await request(server).get('/api/agent/status');

      expect(response.status).toBe(200);
      const blocked = response.body.data.blockedIssues;
      expect(blocked).toHaveLength(1);
      expect(blocked[0].issueNumber).toBe(issue.number);
      expect(blocked[0].blockedReason).toBe('Test blocked reason');
      expect(blocked[0].retryCount).toBe(2);
      expect(blocked[0].stage).toBeDefined();
    });

    it('should return empty blockedIssues when none blocked', async () => {
      const response = await request(server).get('/api/agent/status');

      expect(response.status).toBe(200);
      expect(response.body.data.blockedIssues).toEqual([]);
    });
  });

  describe('POST /api/issues/:number/merge', () => {
    let server: http.Server;

    afterEach(() => {
      server?.close();
    });

    function createMergeApp(worktreeManager: any): http.Server {
      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        worktreeManager,
        undefined,
        undefined,
        undefined,
        undefined,
        undefined,
        undefined,
        undefined,
        undefined,
        undefined,
        undefined,
        undefined,
      ));
      return createTestServer(app);
    }

    it('rejects direct merge when issue is not in Integrate stage with 409', async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/tmp/test-project' });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Merge Me' });
      const worktreeManager = {
        exists: vi.fn().mockReturnValue(true),
      };
      server = createMergeApp(worktreeManager);

      const response = await request(server).post(`/api/issues/${issue.number}/merge`);

      expect(response.status).toBe(409);
      expect(response.body.success).toBe(false);
      expect(response.body.error).toContain('Direct merge is not allowed');
      expect(response.body.error).toContain('Use Check approval');
    });

it('allows merge when issue is in Integrate stage and enqueues resume-pipeline', async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/tmp/test-project' });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Merge Me' });
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateStage(issue.id, Stage.Integrate);
      issueRepo.setMergeState(issue.id, MergeState.Merged);

      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, undefined, undefined, stateManager.getIssueTaskQueueRepo());

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        agentRunner,
      ));
      server = createTestServer(app);

      const response = await request(server).post(`/api/issues/${issue.number}/merge`);

      expect(response.status).toBe(202);
      expect(response.body.success).toBe(true);
      expect(response.body.data.message).toContain('routed to Integrate');
    });

    it('returns error when AgentRunnerService not configured', async () => {
      const project = await projectService.create({ name: 'Test Project', path: '/tmp/test-project' });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Merge Me' });
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateStage(issue.id, Stage.Integrate);
      issueRepo.setMergeState(issue.id, MergeState.Merged);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        undefined,
      ));
      server = createTestServer(app);

      const response = await request(server).post(`/api/issues/${issue.number}/merge`);

      expect(response.status).toBe(500);
      expect(response.body.error).toContain('AgentRunnerService not configured');
    });
  });

  describe('Issue Commits Routes', () => {
    let server: http.Server;
    let projectId: string;
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
      tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-commits-test-'));
      repoDir = path.join(tmpDir, 'repo');
      fs.mkdirSync(repoDir);
      await initGitRepo(repoDir);

      const { WorktreeManager } = await import('../src/git/worktree-manager');
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);
      const wm = new WorktreeManager();
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, wm, undefined, undefined, agentRunner));
      server = createTestServer(app);

      const project = await projectService.create({ name: 'Test Project', path: repoDir });
      projectId = project.id;
      projectService.setCurrent(project);
    });

    afterEach(() => {
      server?.close();
      fs.rmSync(tmpDir, { recursive: true, force: true });
    });

    describe('GET /api/issues/:number/commits', () => {
      it('should return 400 when no active project', async () => {
        projectService.clearCurrent();
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).get('/api/issues/1/commits');

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('No active project');
      });

      it('should return 404 when issue not found', async () => {
        const response = await request(server).get('/api/issues/999/commits');

        expect(response.status).toBe(404);
        expect(response.body.error).toContain('not found');
      });

      it('should return unavailable when no draft worktree exists', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).get('/api/issues/1/commits');

        expect(response.status).toBe(200);
        expect(response.body.data.available).toBe(false);
        expect(response.body.data.reason).toBe('not_started');
      });

      it('should return commits with correct fields', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const git = promisify(execFile);
        await git('git', ['checkout', '-b', 'mo/issue-1'], { cwd: repoDir });
        fs.writeFileSync(path.join(repoDir, 'test.txt'), 'hello');
        await git('git', ['add', '-A'], { cwd: repoDir });
        await git('git', ['commit', '-m', 'add test file'], { cwd: repoDir });
        await git('git', ['checkout', 'main'], { cwd: repoDir });

        const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'test-project', 'worktrees', 'issue-1');
        fs.mkdirSync(worktreeDir, { recursive: true });

        const response = await request(server).get('/api/issues/1/commits');

        expect(response.status).toBe(200);
        expect(response.body.data.commits.length).toBeGreaterThanOrEqual(1);

        const commit = response.body.data.commits[0];
        expect(commit.hash).toBeDefined();
        expect(commit.message).toBe('add test file');
        expect(commit.author).toBe('Test');
        expect(commit.date).toBeDefined();
        expect(typeof commit.filesChanged).toBe('number');
        expect(typeof commit.additions).toBe('number');
        expect(typeof commit.deletions).toBe('number');

        fs.rmSync(worktreeDir, { recursive: true, force: true });
      });
    });

    describe('GET /api/issues/:number/commits/:hash/diff', () => {
      it('should return 400 when no active project', async () => {
        projectService.clearCurrent();
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).get('/api/issues/1/commits/abc1234/diff');

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('No active project');
      });

      it('should return 400 for invalid hash format', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).get('/api/issues/1/commits/not-a-hash/diff');

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('Invalid commit hash');
      });

      it('should return 404 when issue not found', async () => {
        const response = await request(server).get('/api/issues/999/commits/abc1234/diff');

        expect(response.status).toBe(404);
        expect(response.body.error).toContain('not found');
      });

      it('should return unavailable when no draft worktree exists', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const response = await request(server).get('/api/issues/1/commits/abc1234/diff');

        expect(response.status).toBe(200);
        expect(response.body.data.available).toBe(false);
        expect(response.body.data.reason).toBe('not_started');
      });
    });

    describe('GET /api/issues/:number/file-content', () => {
      it('returns available side when reading added or deleted files', async () => {
        await issueService.create({ projectId, title: 'Test Issue' });

        const git = promisify(execFile);
        await git('git', ['checkout', '-b', 'mo/issue-1'], { cwd: repoDir });
        fs.writeFileSync(path.join(repoDir, 'added.txt'), 'added content\n');
        fs.unlinkSync(path.join(repoDir, 'README.md'));
        await git('git', ['add', '-A'], { cwd: repoDir });
        await git('git', ['commit', '-m', 'change files'], { cwd: repoDir });
        await git('git', ['checkout', 'main'], { cwd: repoDir });

        const worktreeDir = path.join(os.homedir(), '.mohist', 'projects', 'test-project', 'worktrees', 'issue-1');
        fs.mkdirSync(worktreeDir, { recursive: true });

        const addedResponse = await request(server).get('/api/issues/1/file-content?path=added.txt');
        expect(addedResponse.status).toBe(200);
        expect(addedResponse.body.data.base).toBe('');
        expect(addedResponse.body.data.head).toBe('added content\n');

        const deletedResponse = await request(server).get('/api/issues/1/file-content?path=README.md');
        expect(deletedResponse.status).toBe(200);
        expect(deletedResponse.body.data.base).toBe('init');
        expect(deletedResponse.body.data.head).toBe('');

        fs.rmSync(worktreeDir, { recursive: true, force: true });
      });
    });
  });
});
