import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import http from 'node:http';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { CheckSuiteRepo } from '../src/db/check-suite-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { EventBus, AgentRunnerService } from '../src/services';
import { StateManager } from '../src/server/state-manager';
import { createIssueRoutes } from '../src/api/issues';
import { Stage, IssueStatus } from '../src/types';
import { WorkflowController, type ChangeArtifactsManager } from '../src/workflow/workflow-controller';
import type { EventBus as EventBusType } from '../src/services/event-bus';
import type { IssueRepo as IssueRepoType } from '../src/db/issue-repo';
import { execFile as cpExecFile, execFileSync as cpExecFileSync } from 'child_process';

vi.mock('../src/agent-runtime/acp-session', () => ({
  createAcpConnection: vi.fn(),
}));

vi.mock('../src/openspec/ralph-executor', () => ({
  RalphExecutor: vi.fn().mockImplementation(() => ({
    execute: vi.fn().mockResolvedValue({ success: true, completed: 1, failed: 0, total: 1 }),
  })),
}));

vi.mock('../src/openspec/detector', () => ({
  detectOpenSpecChange: vi.fn().mockReturnValue({
    changePath: '/tmp/change',
    tasksPath: '/tmp/change/tasks.json',
    sessionMemoriesPath: '/tmp/change/session-memories',
    proposalPath: '/tmp/change/proposal.md',
    designPath: '/tmp/change/design.md',
    specsPath: '/tmp/change/specs',
  }),
  findChangeDir: vi.fn().mockReturnValue('/tmp/change'),
}));

vi.mock('fs', () => ({
  existsSync: vi.fn((p: string) => {
    if (typeof p === 'string' && (p.endsWith('review.md') || p.endsWith('review-self-check.md'))) return false;
    return true;
  }),
  readdirSync: vi.fn().mockReturnValue([]),
  rmSync: vi.fn(),
  mkdirSync: vi.fn(),
  writeFileSync: vi.fn(),
  readFileSync: vi.fn((p: string) => {
    if (typeof p === 'string' && (p.endsWith('review.md') || p.endsWith('review-self-check.md'))) {
      return '## Result: PASS\nAll checks passed.';
    }
    if (typeof p === 'string' && p.endsWith('tasks.json')) {
      return JSON.stringify({ version: 1, tasks: [] });
    }
    return '{}';
  }),
}));

vi.mock('child_process', () => ({
  execFile: vi.fn((...args: unknown[]) => {
    const lastArg = args[args.length - 1];
    const callback = typeof lastArg === 'function' ? lastArg : undefined;
    if (callback) callback(null, { stdout: '', stderr: '' });
  }),
  execFileSync: vi.fn(() => 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n'),
}));

vi.mock('../src/config/config-loader', () => ({
  load: vi.fn().mockReturnValue({}),
  clearConfigCache: vi.fn(),
  getAgentTimeoutConfig: vi.fn().mockReturnValue({ taskTimeout: 600, stageTimeout: 3600, maxGracePeriods: 2 }),
  getProviderConfig: vi.fn().mockReturnValue({
    sdk: 'openai-compatible',
    name: 'test',
    apiKey: null,
    baseURL: null,
    envVars: [],
    source: 'none',
  }),
  getServerConfig: vi.fn().mockReturnValue({ port: 3456, host: '127.0.0.1' }),
  getLogConfig: vi.fn().mockReturnValue({ level: 'INFO' }),
  getConfigPath: vi.fn().mockReturnValue('/tmp/test-config.jsonc'),
  getConfigDir: vi.fn().mockReturnValue('/tmp/test-config-dir'),
  resolveOpencodeBinPath: vi.fn().mockReturnValue(undefined),
  writeConfig: vi.fn(),
}));

vi.mock('../src/agents/artifact-prompt', () => ({
  buildArtifactPrompt: vi.fn().mockReturnValue('mock-prompt'),
  buildSelfReviewPrompt: vi.fn().mockReturnValue('mock-self-review-prompt'),
  buildReviewerPrompt: vi.fn().mockReturnValue('mock-reviewer-prompt'),
  buildReviewSelfCheckPrompt: vi.fn().mockReturnValue('mock-review-self-check-prompt'),
  buildAutoFixPrompt: vi.fn().mockReturnValue('mock-auto-fix-prompt'),
  buildReVerifyPrompt: vi.fn().mockReturnValue('mock-reverify-prompt'),
}));

vi.mock('../src/agents/agent-prompt-schema', () => ({
  formatAgentPrompt: vi.fn().mockReturnValue('mock-formatted-prompt'),
}));

vi.mock('../src/workflow/workflow-loader', () => ({
  loadWorkflow: vi.fn().mockReturnValue('default'),
  loadAgentConfig: vi.fn().mockReturnValue({}),
  loadChecksConfig: vi.fn().mockReturnValue({
    buildTest: { command: 'npm test', timeout: 60000, autoFix: false, maxFixAttempts: 0 },
    aiReview: { enabled: false },
  }),
  DEFAULT_CHECKS_CONFIG: {
    buildTest: { command: 'npm test', timeout: 60000, autoFix: false, maxFixAttempts: 0 },
    aiReview: { enabled: false },
  },
}));

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

function setupExecFileMock(callbackStyle: 'succeed' | 'fail'): void {
  const execFile = vi.mocked(cpExecFile);
  if (callbackStyle === 'succeed') {
    execFile.mockImplementation(
      (...args: unknown[]) => {
        const lastArg = args[args.length - 1];
        const callback = typeof lastArg === 'function' ? lastArg : undefined;
        if (callback) callback(null, { stdout: 'build ok', stderr: '' });
      }
    );
  } else {
    execFile.mockImplementation(
      (...args: unknown[]) => {
        const lastArg = args[args.length - 1];
        const callback = typeof lastArg === 'function' ? lastArg : undefined;
        if (callback) {
          const err = new Error('test failed') as any;
          err.killed = false;
          err.stdout = '';
          err.stderr = 'test error output';
          callback(err, { stdout: '', stderr: 'test error output' });
        }
      }
    );
  }
}

describe('CheckSuiteRepo', () => {
  let db: DatabaseManager;
  let repo: CheckSuiteRepo;
  let issueId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });

    const issueRepo = new IssueRepo(db);
    const issue = issueRepo.create({ number: 1, projectId: project.id, title: 'Test Issue' });
    issueId = issue.id;

    repo = new CheckSuiteRepo(db);
  });

  afterEach(() => {
    db.close();
  });

  describe('create', () => {
    it('should create a CheckSuite with correct initial state', () => {
      const sha = 'a'.repeat(40);
      const suite = repo.create({ issueId, snapshotSha: sha });

      expect(suite.id).toBeDefined();
      expect(suite.issueId).toBe(issueId);
      expect(suite.snapshotSha).toBe(sha);
      expect(suite.status).toBe('running');
      expect(suite.checks['build-test'].status).toBe('pending');
      expect(suite.checks['ai-review'].status).toBe('pending');
      expect(suite.createdAt).toBeDefined();
      expect(suite.updatedAt).toBeDefined();
    });

    it('should only contain build-test and ai-review checks (no merge-ready)', () => {
      const sha = 'a'.repeat(40);
      const suite = repo.create({ issueId, snapshotSha: sha });

      const checkNames = Object.keys(suite.checks);
      expect(checkNames).toEqual(['build-test', 'ai-review']);
      expect((checkNames as string[]).includes('merge-ready')).toBe(false);
    });
  });

  describe('findActiveByIssueId', () => {
    it('should find active suite by issue id', () => {
      const sha = 'a'.repeat(40);
      repo.create({ issueId, snapshotSha: sha });

      const found = repo.findActiveByIssueId(issueId);

      expect(found).not.toBeNull();
      expect(found!.snapshotSha).toBe(sha);
      expect(found!.status).toBe('running');
    });

    it('should return null when no active suite exists', () => {
      expect(repo.findActiveByIssueId('nonexistent')).toBeNull();
    });

    it('should not find suites with passed/failed status', () => {
      const sha = 'a'.repeat(40);
      const suite = repo.create({ issueId, snapshotSha: sha });
      repo.updateStatus(suite.id, 'failed');

      expect(repo.findActiveByIssueId(issueId)).toBeNull();
    });

    it('should find suites with awaiting-approval status', () => {
      const sha = 'a'.repeat(40);
      const suite = repo.create({ issueId, snapshotSha: sha });
      repo.updateStatus(suite.id, 'awaiting-approval');

      const found = repo.findActiveByIssueId(issueId);
      expect(found).not.toBeNull();
      expect(found!.status).toBe('awaiting-approval');
    });
  });

  describe('updateChecks', () => {
    it('should update a single check state', () => {
      const sha = 'a'.repeat(40);
      const suite = repo.create({ issueId, snapshotSha: sha });

      const updated = repo.updateChecks(suite.id, 'build-test', {
        status: 'running',
      });

      expect(updated!.checks['build-test'].status).toBe('running');
      expect(updated!.checks['ai-review'].status).toBe('pending');
    });

    it('should persist check result with output and ranAt', () => {
      const sha = 'a'.repeat(40);
      const suite = repo.create({ issueId, snapshotSha: sha });
      const ranAt = new Date().toISOString();

      repo.updateChecks(suite.id, 'build-test', {
        status: 'passed',
        output: { summary: 'All tests passed' },
        ranAt,
      });

      const found = repo.findById(suite.id);
      expect(found!.checks['build-test'].status).toBe('passed');
      expect(found!.checks['build-test'].ranAt).toBe(ranAt);
      expect((found!.checks['build-test'].output as any).summary).toBe('All tests passed');
    });
  });

  describe('updateStatus', () => {
    it('should update suite status', () => {
      const sha = 'a'.repeat(40);
      const suite = repo.create({ issueId, snapshotSha: sha });

      const updated = repo.updateStatus(suite.id, 'awaiting-approval');

      expect(updated!.status).toBe('awaiting-approval');
    });
  });

  describe('updateSnapshotSha', () => {
    it('should update snapshot SHA and reset checks to pending', () => {
      const sha1 = 'a'.repeat(40);
      const sha2 = 'b'.repeat(40);
      const suite = repo.create({ issueId, snapshotSha: sha1 });

      repo.updateChecks(suite.id, 'build-test', { status: 'passed', ranAt: new Date().toISOString() });

      const updated = repo.updateSnapshotSha(suite.id, sha2);

      expect(updated!.snapshotSha).toBe(sha2);
      expect(updated!.checks['build-test'].status).toBe('pending');
      expect(updated!.checks['ai-review'].status).toBe('pending');
      expect(updated!.status).toBe('running');
    });
  });

  describe('resetChecks', () => {
    it('should reset all checks to pending and status to running', () => {
      const sha = 'a'.repeat(40);
      const suite = repo.create({ issueId, snapshotSha: sha });

      repo.updateChecks(suite.id, 'build-test', { status: 'failed' });
      repo.updateStatus(suite.id, 'failed');

      const reset = repo.resetChecks(suite.id);

      expect(reset!.checks['build-test'].status).toBe('pending');
      expect(reset!.checks['ai-review'].status).toBe('pending');
      expect(reset!.status).toBe('running');
    });
  });

  describe('findById', () => {
    it('should return null for nonexistent id', () => {
      expect(repo.findById('nonexistent')).toBeNull();
    });
  });
});

describe('CheckSuiteRepo multiple issue isolation', () => {
  let db: DatabaseManager;
  let repo: CheckSuiteRepo;
  let issueId1: string;
  let issueId2: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });

    const issueRepo = new IssueRepo(db);
    const issue1 = issueRepo.create({ number: 1, projectId: project.id, title: 'Issue 1' });
    const issue2 = issueRepo.create({ number: 2, projectId: project.id, title: 'Issue 2' });
    issueId1 = issue1.id;
    issueId2 = issue2.id;

    repo = new CheckSuiteRepo(db);
  });

  afterEach(() => {
    db.close();
  });

  it('should isolate suites between issues', () => {
    const sha = 'a'.repeat(40);
    repo.create({ issueId: issueId1, snapshotSha: sha });
    repo.create({ issueId: issueId2, snapshotSha: sha });

    const found1 = repo.findActiveByIssueId(issueId1);
    const found2 = repo.findActiveByIssueId(issueId2);

    expect(found1).not.toBeNull();
    expect(found2).not.toBeNull();
    expect(found1!.id).not.toBe(found2!.id);
  });
});

describe('Check stage creates CheckSuite and persists check states', () => {
  let db: DatabaseManager;
  let checkSuiteRepo: CheckSuiteRepo;
  let realIssueId: string;
  let realProjectId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });
    realProjectId = project.id;

    const issueRepo = new IssueRepo(db);
    const issue = issueRepo.create({ number: 1, projectId: realProjectId, title: 'Test Issue' });
    realIssueId = issue.id;

    checkSuiteRepo = new CheckSuiteRepo(db);
    vi.clearAllMocks();
  });

  afterEach(() => {
    db.close();
  });

  it('should create CheckSuite with snapshotSha on check stage entry and persist check states', async () => {
    const sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
    vi.mocked(cpExecFileSync).mockReturnValue(sha + '\n');

    setupExecFileMock('succeed');

    const { createAcpConnection } = await import('../src/agent-runtime/acp-session');
    const mockConn = {
      prompt: vi.fn().mockResolvedValue({ text: 'PASS', success: true }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const eventBus = {
      emit: vi.fn(),
      on: vi.fn(),
      off: vi.fn(),
      removeAllListeners: vi.fn(),
    } as unknown as EventBusType;

    const artifactManager: ChangeArtifactsManager = {
      getChangeDir: vi.fn().mockReturnValue('/tmp/change'),
      createChangeDir: vi.fn().mockReturnValue('/tmp/change'),
      readArtifact: vi.fn().mockReturnValue(null),
      writeArtifact: vi.fn().mockReturnValue(true),
      exists: vi.fn().mockReturnValue(true),
      readTasks: vi.fn().mockReturnValue(null),
      updateTaskPasses: vi.fn().mockReturnValue(true),
    };

    const issue = {
      id: realIssueId,
      number: 1,
      title: 'Test Issue',
      body: '',
      stage: Stage.Check,
      status: IssueStatus.Active,
      projectId: realProjectId,
      labels: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };

    const controller = new WorkflowController({
      artifactManager,
      worktreePath: '/tmp/worktree',
      eventBus,
      projectId: realProjectId,
      checkSuiteRepo,
    });

    const result = await controller.runPipelineCheckStage(issue, {
      cwd: '/tmp/worktree',
      issueId: realIssueId,
      issueNumber: 1,
      projectId: realProjectId,
    });

    expect(result.success).toBe(true);
    expect(result.requiresApproval).toBe(true);

    const suite = checkSuiteRepo.findActiveByIssueId(realIssueId);
    expect(suite).not.toBeNull();
    expect(suite!.snapshotSha).toBe(sha);
    expect(suite!.status).toBe('awaiting-approval');
    expect(suite!.checks['build-test'].status).toBe('passed');
    expect(suite!.checks['build-test'].ranAt).toBeDefined();
  });
}, 30000);

describe('Approve endpoint SHA validation', () => {
  let db: DatabaseManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stateManager: StateManager;
  let checkSuiteRepo: CheckSuiteRepo;
  let projectId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);

    const projectRepo = stateManager.getProjectRepo();
    const issueRepo = stateManager.getIssueRepo();
    const configRepo = stateManager.getConfigRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();

    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);
    checkSuiteRepo = new CheckSuiteRepo(db);
  });

  afterEach(() => {
    vi.clearAllMocks();
    db.close();
  });

  async function setupIssueWithApproval(approvalStage: Stage): Promise<{ issueId: string; number: number }> {
    const project = await projectService.create({ name: 'Test ' + Date.now(), path: '/test-' + Date.now() });
    projectId = project.id;
    projectService.setCurrent(project);

    const issue = issueService.create({ projectId, title: 'Test Issue' });
    issueService.transitionToStage(issue.id, approvalStage);
    const issueRepo = stateManager.getIssueRepo();
    issueRepo.setApprovalState(issue.id, {
      stage: approvalStage,
      status: 'awaiting',
      output: {},
      requestedAt: new Date().toISOString(),
    });

    return { issueId: issue.id, number: issue.number };
  }

  it('should return 200 on SHA match and enqueue for merge', async () => {
    const { number, issueId } = await setupIssueWithApproval(Stage.Check);
    const sha = 'a'.repeat(40);

    checkSuiteRepo.create({ issueId, snapshotSha: sha });

    vi.mocked(cpExecFile).mockImplementation(
      (...args: unknown[]) => {
        const lastArg = args[args.length - 1];
        const callback = typeof lastArg === 'function' ? lastArg : undefined;
        if (callback) callback(null, { stdout: sha, stderr: '' });
      }
    );

    const eventBus = new EventBus();
    const agentRunner = new AgentRunnerService(eventBus);
    vi.spyOn(agentRunner, 'hasPendingGate').mockReturnValue(true);

    const mergeQueue = {
      enqueue: vi.fn(),
      getStatus: vi.fn().mockReturnValue([]),
    };

    const worktreeManager = {
      getPath: vi.fn().mockReturnValue('/tmp/worktree'),
    };

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService,
      projectService,
      stateManager,
      worktreeManager as any,
      undefined,
      undefined,
      agentRunner,
      undefined,
      undefined,
      undefined,
      undefined,
      mergeQueue as any,
      undefined,
      undefined,
      checkSuiteRepo,
    ));

    const server = createTestServer(app);

    const response = await request(server)
      .post(`/api/issues/${number}/approve`);

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);
    expect(mergeQueue.enqueue).toHaveBeenCalledWith(projectId, number);
  }, 15000);

  it('should return 202 on SHA mismatch and trigger rerun', async () => {
    const { number, issueId } = await setupIssueWithApproval(Stage.Check);

    const oldSha = 'a'.repeat(40);
    const newSha = 'b'.repeat(40);

    checkSuiteRepo.create({ issueId, snapshotSha: oldSha });

    vi.mocked(cpExecFile).mockImplementation(
      (...args: unknown[]) => {
        const lastArg = args[args.length - 1];
        const callback = typeof lastArg === 'function' ? lastArg : undefined;
        if (callback) callback(null, { stdout: newSha, stderr: '' });
      }
    );

    const eventBus = new EventBus();
    const agentRunner = new AgentRunnerService(eventBus);
    vi.spyOn(agentRunner, 'hasPendingGate').mockReturnValue(true);
    vi.spyOn(agentRunner, 'resumePipeline').mockImplementation(() => {});
    vi.spyOn(agentRunner, 'isRunning').mockReturnValue(false);

    const mergeQueue = {
      enqueue: vi.fn(),
      getStatus: vi.fn().mockReturnValue([]),
    };

    const worktreeManager = {
      getPath: vi.fn().mockReturnValue('/tmp/worktree'),
    };

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService,
      projectService,
      stateManager,
      worktreeManager as any,
      undefined,
      undefined,
      agentRunner,
      undefined,
      undefined,
      undefined,
      undefined,
      mergeQueue as any,
      undefined,
      undefined,
      checkSuiteRepo,
    ));

    const server = createTestServer(app);

    const response = await request(server)
      .post(`/api/issues/${number}/approve`);

    expect(response.status).toBe(202);
    expect(response.body.data.message).toContain('changed');
    expect(mergeQueue.enqueue).not.toHaveBeenCalled();
  }, 15000);

  it('should return 200 with no active CheckSuite (recovery path)', async () => {
    const { number } = await setupIssueWithApproval(Stage.Check);

    const eventBus = new EventBus();
    const agentRunner = new AgentRunnerService(eventBus);
    vi.spyOn(agentRunner, 'hasPendingGate').mockReturnValue(true);

    const mergeQueue = {
      enqueue: vi.fn(),
      getStatus: vi.fn().mockReturnValue([]),
    };

    const worktreeManager = {
      getPath: vi.fn().mockReturnValue('/tmp/worktree'),
    };

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService,
      projectService,
      stateManager,
      worktreeManager as any,
      undefined,
      undefined,
      agentRunner,
      undefined,
      undefined,
      undefined,
      undefined,
      mergeQueue as any,
      undefined,
      undefined,
      checkSuiteRepo,
    ));

    const server = createTestServer(app);

    const response = await request(server)
      .post(`/api/issues/${number}/approve`);

    expect(response.status).toBe(200);
    expect(mergeQueue.enqueue).toHaveBeenCalledWith(projectId, number);
  }, 15000);
});

describe('Check stage loop maxRetries=3', () => {
  let db: DatabaseManager;
  let realIssueId: string;
  let realProjectId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });
    realProjectId = project.id;

    const issueRepo = new IssueRepo(db);
    const issue = issueRepo.create({ number: 1, projectId: realProjectId, title: 'Test Issue' });
    realIssueId = issue.id;

    vi.clearAllMocks();
  });

  afterEach(() => {
    db.close();
  });

  it('should limit loop iterations to maxRetries=3 when build-test always fails', async () => {
    const sha = 'a'.repeat(40);
    vi.mocked(cpExecFileSync).mockReturnValue(sha + '\n');

    setupExecFileMock('fail');

    const { createAcpConnection } = await import('../src/agent-runtime/acp-session');
    const mockConn = {
      prompt: vi.fn().mockResolvedValue({ text: 'fix applied', success: true }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const checkSuiteRepo = new CheckSuiteRepo(db);

    const eventBus = {
      emit: vi.fn(),
      on: vi.fn(),
      off: vi.fn(),
      removeAllListeners: vi.fn(),
    } as unknown as EventBusType;

    const artifactManager: ChangeArtifactsManager = {
      getChangeDir: vi.fn().mockReturnValue('/tmp/change'),
      createChangeDir: vi.fn().mockReturnValue('/tmp/change'),
      readArtifact: vi.fn().mockReturnValue(null),
      writeArtifact: vi.fn().mockReturnValue(true),
      exists: vi.fn().mockReturnValue(true),
      readTasks: vi.fn().mockReturnValue(null),
      updateTaskPasses: vi.fn().mockReturnValue(true),
    };

    const issue = {
      id: realIssueId,
      number: 1,
      title: 'Test',
      body: '',
      stage: Stage.Check,
      status: IssueStatus.Active,
      projectId: realProjectId,
      labels: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };

    const controller = new WorkflowController({
      artifactManager,
      worktreePath: '/tmp/worktree',
      eventBus,
      projectId: realProjectId,
      checkSuiteRepo,
    });

    const result = await controller.runPipelineCheckStage(issue, {
      cwd: '/tmp/worktree',
      issueId: realIssueId,
      issueNumber: 1,
      projectId: realProjectId,
    });

    expect(result.success).toBe(false);
    expect(result.escalateToStage).toBe(Stage.Plan);
    expect(result.message).toContain('3 attempts');

    const suite = checkSuiteRepo.findActiveByIssueId(realIssueId);
    if (suite) {
      expect(suite.status).toBe('failed');
    }
  }, 30000);
});
