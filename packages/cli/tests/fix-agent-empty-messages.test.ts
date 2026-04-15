import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { SessionManager } from '../src/agent-runtime/session';
import { ToolRegistry } from '../src/agent-runtime/tool';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { CommentRepo } from '../src/db/comment-repo';
import { Stage, IssueStatus } from '../src/types';
import { EventBus } from '../src/services/event-bus';
import { AgentRunnerService } from '../src/services/agent-runner-service';
import http from 'node:http';
import { Hono } from 'hono';
import request from 'supertest';
import { StateManager } from '../src/server/state-manager';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { createIssueRoutes } from '../src/api/issues';

const mockStreamText = vi.hoisted(() => vi.fn());

vi.mock('ai', async (importOriginal) => {
  const actual = await importOriginal() as any;
  return {
    ...actual,
    streamText: mockStreamText,
  };
});

import { runAgentLoop } from '../src/agent-runtime/agent-loop';

function createMockStreamTextResult(parts: any[]) {
  return {
    fullStream: (async function* () { for (const p of parts) yield p; })(),
    text: Promise.resolve('done'),
    steps: Promise.resolve([{ response: { messages: [] } }]),
    finishReason: Promise.resolve('stop'),
  };
}

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

describe('fix-agent-empty-messages', () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  describe('T-001: Inject initial message when session messages is empty', () => {
    it('should inject initial user message when session.messages is empty', async () => {
      const sessionManager = new SessionManager();
      const toolRegistry = new ToolRegistry();
      const session = sessionManager.create(1);

      expect(session.messages).toHaveLength(0);

      mockStreamText.mockReturnValue(
        createMockStreamTextResult([{ type: 'text-delta', text: 'working...' }])
      );

      await runAgentLoop(session, sessionManager, toolRegistry, {} as any);

      expect(session.messages).toHaveLength(1);
      expect(session.messages[0]).toEqual({
        role: 'user',
        content: expect.stringContaining('Start working on the current issue'),
      });
      expect(mockStreamText).toHaveBeenCalledWith(
        expect.objectContaining({
          messages: expect.arrayContaining([
            expect.objectContaining({ role: 'user' }),
          ]),
        })
      );
    });

    it('should not inject message when session already has messages', async () => {
      const sessionManager = new SessionManager();
      const toolRegistry = new ToolRegistry();
      const session = sessionManager.create(1);

      sessionManager.appendMessage(session.id, {
        role: 'user',
        content: 'Existing message',
      });

      mockStreamText.mockReturnValue(
        createMockStreamTextResult([{ type: 'text-delta', text: 'working...' }])
      );

      await runAgentLoop(session, sessionManager, toolRegistry, {} as any);

      expect(session.messages).toHaveLength(1);
      expect(session.messages[0].content).toBe('Existing message');
    });
  });

  describe('T-002: Roll back stage to Draft on agent failure', () => {
    let db: DatabaseManager;
    let issueRepo: IssueRepo;
    let commentRepo: CommentRepo;
    let eventBus: EventBus;

    beforeEach(() => {
      db = new DatabaseManager({ inMemory: true });
      initializeDatabase(db);
      issueRepo = new IssueRepo(db);
      commentRepo = new CommentRepo(db);
      eventBus = new EventBus();
    });

    afterEach(() => {
      db.close();
    });

    it('should roll back stage to Draft when agent fails', async () => {
      const projectRepo = new ProjectRepo(db);
      const project = projectRepo.create({ name: 'Test', path: '/test' });
      const issueService = new IssueService(issueRepo, commentRepo);
      const issue = issueService.create({ projectId: project.id, title: 'Test' });

      issueService.transitionToStage(issue.id, Stage.Plan);

      const agentErrorPromise = new Promise<string>((resolve) => {
        eventBus.on('agent_error', (data) => resolve(data.error));
      });

      const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

      const mockSessionManager = {
        create: vi.fn().mockReturnValue({ id: 's1', messages: [], status: 'active' }),
        appendMessage: vi.fn(),
        pause: vi.fn(),
        resume: vi.fn(),
        close: vi.fn(),
      } as any;

      const startResult = service.start(
        issue,
        project.id,
        issueRepo,
        commentRepo,
        undefined,
        '/test',
        mockSessionManager,
        undefined,
        (issueId, status) => issueService.setStatus(issueId, status),
      );

      expect(startResult.started).toBe(true);

      const error = await agentErrorPromise;
      expect(error).toBeTruthy();

      const updated = issueRepo.findById(issue.id);
      expect(updated?.status).toBe(IssueStatus.Blocked);
      expect(updated?.stage).toBe(Stage.Draft);
      expect(issueRepo.findPendingApprovalByIssueId(issue.id)).toBeNull();
    }, 30000);
  });

  describe('T-003: Reopen endpoint resume and reset logic', () => {
    let db: DatabaseManager;
    let stateManager: StateManager;
    let projectService: ProjectService;
    let issueService: IssueService;
    let agentRunner: AgentRunnerService;
    let eventBus: EventBus;
    let server: http.Server;
    let projectId: string;

    beforeEach(async () => {
      db = new DatabaseManager({ inMemory: true });
      stateManager = new StateManager(db);
      projectService = new ProjectService(
        stateManager.getProjectRepo(),
        stateManager.getConfigRepo(),
        stateManager.getIssueRepo(),
        stateManager.getLabelRepo(),
      );
      issueService = new IssueService(
        stateManager.getIssueRepo(),
        stateManager.getCommentRepo(),
      );
      eventBus = new EventBus();
      agentRunner = new AgentRunnerService(eventBus);

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(
        issueService, projectService, stateManager,
        undefined, undefined, undefined, agentRunner,
      ));
      server = createTestServer(app);

      const project = await projectService.create({ name: 'Test', path: '/test' });
      projectId = project.id;
      projectService.setCurrent(project);
    });

    afterEach(() => {
      db.close();
    });

    it('should reset stage to Draft when no paused session', async () => {
      const issue = issueService.create({ projectId, title: 'Blocked issue' });
      stateManager.getIssueRepo().updateStage(issue.id, Stage.Plan);
      issueService.setStatus(issue.id, IssueStatus.Blocked);
      stateManager.getIssueRepo().setApprovalState(issue.id, {
        status: 'awaiting',
        requestedAt: new Date(),
        requestedBy: 'user',
      });

      expect(agentRunner.hasPausedSession(issue.number)).toBe(false);

      const response = await request(server).post(`/api/issues/${issue.number}/reopen`);

      expect(response.status).toBe(200);
      expect(response.body.data.message).toContain('reset to draft');

      const updated = issueService.getByNumber(projectId, issue.number);
      expect(updated?.stage).toBe(Stage.Draft);
      expect(updated?.status).toBe(IssueStatus.Active);
      expect(stateManager.getIssueRepo().findPendingApprovalByIssueId(issue.id)).toBeNull();
    });

    it('should return 409 when agent is already running', async () => {
      const issue = issueService.create({ projectId, title: 'Running issue' });
      issueService.setStatus(issue.id, IssueStatus.Blocked);

      const mockSession = { id: 'paused-1', messages: [], status: 'paused' };
      (agentRunner as any).pausedSessions.set(issue.number, mockSession);
      (agentRunner as any).activeAgents.set(issue.id, {
        issueId: issue.id,
        issueNumber: issue.number,
        promise: Promise.resolve(),
        projectId,
      });

      const response = await request(server).post(`/api/issues/${issue.number}/reopen`);

      expect(response.status).toBe(409);
      expect(response.body.error).toContain('already has an agent running');
    });

    it('should only change status when agentRunner is not available', async () => {
      const localApp = new Hono();
      localApp.route('/api/issues', createIssueRoutes(
        issueService, projectService, stateManager,
      ));
      const localServer = createTestServer(localApp);

      const issue = issueService.create({ projectId, title: 'No runner' });
      stateManager.getIssueRepo().updateStage(issue.id, Stage.Plan);
      issueService.setStatus(issue.id, IssueStatus.Blocked);

      const response = await request(localServer).post(`/api/issues/${issue.number}/reopen`);

      expect(response.status).toBe(200);
      expect(response.body.data.message).toBe(`Issue #${issue.number} reopened`);

      const updated = issueService.getByNumber(projectId, issue.number);
      expect(updated?.status).toBe(IssueStatus.Active);
      expect(updated?.stage).toBe(Stage.Plan);
    });
  });
});
