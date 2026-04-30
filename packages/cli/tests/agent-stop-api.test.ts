import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import http from 'node:http';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { EventBus, AgentRunnerService } from '../src/services';
import { StateManager } from '../src/server/state-manager';
import { createIssueRoutes } from '../src/api/issues';
import { Stage, IssueStatus } from '../src/types';

vi.mock('../src/workflow', () => ({
  WorkflowEngine: class {
    private signal?: AbortSignal;
    constructor(opts: any) {
      this.signal = opts.signal;
    }
    async run() {
      if (this.signal?.aborted) {
        return { completed: false, stage: Stage.Draft, gateRequired: false, message: 'Agent stopped by user' };
      }
      await new Promise<void>((_resolve, reject) => {
        if (this.signal) {
          this.signal.addEventListener('abort', () => {
            reject(new Error('Agent stopped by user'));
          });
        }
      });
      return { completed: true, stage: Stage.Done, gateRequired: false };
    }
  },
  PlanStageRunner: vi.fn(),
  BuildStageRunner: vi.fn(),
  CheckStageRunner: vi.fn(),
  BuildTestCheck: vi.fn(),
  MergeReadyCheck: vi.fn(),
  AiReviewCheck: vi.fn(),
}));

vi.mock('../src/artifacts/change-artifacts-manager', () => ({
  ChangeArtifactsManager: vi.fn().mockImplementation(() => ({})),
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

describe('Agent Stop API', () => {
  let db: DatabaseManager;
  let stateManager: StateManager;
  let issueService: IssueService;
  let projectService: ProjectService;
  let agentRunner: AgentRunnerService;
  let eventBus: EventBus;
  let server: http.Server;
  let projectId: string;

  beforeEach(async () => {
    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);

    const projectRepo = stateManager.getProjectRepo();
    const issueRepo = stateManager.getIssueRepo();
    const configRepo = stateManager.getConfigRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();

    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);

    eventBus = new EventBus();
    agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService,
      projectService,
      stateManager,
      undefined,
      undefined,
      undefined,
      agentRunner,
    ));
    server = createTestServer(app);

    const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
    projectId = project.id;
    projectService.setCurrent(project);
  });

  afterEach(async () => {
    const status = agentRunner.getStatus();
    for (const agent of status.activeAgents) {
      await agentRunner.stop(agent.issueId);
    }
    agentRunner.shutdown();
    db.close();
  });

  function startAgentOnIssue(issueId: string) {
    const issueRepo = stateManager.getIssueRepo();
    const issue = issueService.getById(issueId)!;
    issueRepo.updateStatus(issueId, IssueStatus.Active);
    issueRepo.updateStage(issueId, Stage.Plan);
    const result = agentRunner.startPipeline(
      { ...issue, status: IssueStatus.Active, stage: Stage.Plan },
      projectId,
      issueRepo,
      '/test',
      { cwd: '/test' },
    );
    expect(result.started).toBe(true);
  }

  function setAwaitingApproval(issueId: string, stage: Stage = Stage.Plan) {
    const issueRepo = stateManager.getIssueRepo();
    issueRepo.setApprovalState(issueId, {
      stage,
      status: 'awaiting',
      output: {},
      requestedAt: new Date().toISOString(),
    });
  }

  describe('POST /api/issues/:number/stop', () => {
    it('returns 200 when agent is running and stops it', async () => {
      const issue = issueService.create({ projectId, title: 'Running Issue' });
      startAgentOnIssue(issue.id);

      const response = await request(server).post('/api/issues/1/stop');

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data.message).toContain('stopped');
      expect(agentRunner.isRunning(issue.id)).toBe(false);
    });

    it('returns 409 when no agent running', async () => {
      issueService.create({ projectId, title: 'Idle Issue' });

      const response = await request(server).post('/api/issues/1/stop');

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('No agent running');
    });

    it('returns 404 for non-existent issue', async () => {
      const response = await request(server).post('/api/issues/999/stop');

      expect(response.status).toBe(404);
    });
  });

  describe('POST /api/issues/:number/close with force', () => {
    it('returns 200 and closes after stopping agent with force=true', async () => {
      const issue = issueService.create({ projectId, title: 'Close Force' });
      startAgentOnIssue(issue.id);

      const response = await request(server)
        .post('/api/issues/1/close?force=true')
        .send({});

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(agentRunner.isRunning(issue.id)).toBe(false);

      const closed = issueService.getById(issue.id);
      expect(closed?.status).toBe(IssueStatus.Closed);
    });

    it('returns 409 without force when agent running', async () => {
      const issue = issueService.create({ projectId, title: 'Close No Force' });
      startAgentOnIssue(issue.id);

      const response = await request(server)
        .post('/api/issues/1/close')
        .send({});

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('force=true');
      expect(agentRunner.isRunning(issue.id)).toBe(true);
    });
  });

  describe('POST /api/issues/:number/reopen with force', () => {
    it('returns 200 after stopping agent with force=true', async () => {
      const issue = issueService.create({ projectId, title: 'Reopen Force' });
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.updateStage(issue.id, Stage.Build);
      startAgentOnIssue(issue.id);

      const response = await request(server)
        .post('/api/issues/1/reopen?force=true');

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
    });

    it('returns 409 without force when agent running', async () => {
      const issue = issueService.create({ projectId, title: 'Reopen No Force' });
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      startAgentOnIssue(issue.id);

      const response = await request(server)
        .post('/api/issues/1/reopen');

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('force=true');
    });
  });

  describe('POST /api/issues/:number/approve with force', () => {
    it('stops the running agent with force=true and does not return 409', async () => {
      const issue = issueService.create({ projectId, title: 'Approve Force' });
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateStatus(issue.id, IssueStatus.Active);
      issueRepo.updateStage(issue.id, Stage.Plan);
      startAgentOnIssue(issue.id);
      setAwaitingApproval(issue.id, Stage.Plan);

      const response = await request(server)
        .post('/api/issues/1/approve?force=true');

      expect(response.status).not.toBe(409);
      expect(agentRunner.isRunning(issue.id)).toBe(false);
    });

    it('returns 200 with force=true when no agent running and pending gate exists in DB', async () => {
      const issue = issueService.create({ projectId, title: 'Approve No Agent' });
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateStatus(issue.id, IssueStatus.Active);
      issueRepo.updateStage(issue.id, Stage.Plan);
      setAwaitingApproval(issue.id, Stage.Plan);

      const response = await request(server)
        .post('/api/issues/1/approve?force=true');

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
    });

    it('returns 409 without force when agent running', async () => {
      const issue = issueService.create({ projectId, title: 'Approve No Force' });
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateStatus(issue.id, IssueStatus.Active);
      issueRepo.updateStage(issue.id, Stage.Plan);
      startAgentOnIssue(issue.id);
      setAwaitingApproval(issue.id, Stage.Plan);

      const response = await request(server)
        .post('/api/issues/1/approve');

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('force=true');
    });
  });

  describe('POST /api/issues/:number/reject with force', () => {
    it('returns 200 after stopping agent with force=true', async () => {
      const issue = issueService.create({ projectId, title: 'Reject Force' });
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateStatus(issue.id, IssueStatus.Active);
      issueRepo.updateStage(issue.id, Stage.Plan);
      startAgentOnIssue(issue.id);
      setAwaitingApproval(issue.id, Stage.Plan);

      const response = await request(server)
        .post('/api/issues/1/reject?force=true')
        .send({ message: 'Rejected in test' });

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
    });

    it('returns 409 without force when agent running', async () => {
      const issue = issueService.create({ projectId, title: 'Reject No Force' });
      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateStatus(issue.id, IssueStatus.Active);
      issueRepo.updateStage(issue.id, Stage.Plan);
      startAgentOnIssue(issue.id);
      setAwaitingApproval(issue.id, Stage.Plan);

      const response = await request(server)
        .post('/api/issues/1/reject')
        .send({ message: 'Reject attempt' });

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('force=true');
    });
  });
});
