import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { DatabaseManager } from '../../src/db/database';
import { initializeDatabase } from '../../src/db/migrations';
import { CoderSessionRepo } from '../../src/db/coder-session-repo';
import { SessionStreamLogRepo } from '../../src/db/session-stream-log-repo';
import { WorkflowLogRepo } from '../../src/db/workflow-log-repo';
import { ProjectService } from '../../src/services/project-service';
import { IssueService } from '../../src/services/issue-service';
import { StateManager } from '../../src/server/state-manager';
import { Hono } from 'hono';
import http from 'node:http';
import request from 'supertest';
import { createIssueRoutes } from '../../src/api/issues';
import type { CoderSession } from '../../src/db/coder-session-repo';
import { ProjectRepo } from '../../src/db/project-repo';
import { IssueRepo } from '../../src/db/issue-repo';

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

describe('Session List API - Summary/Detail Split', () => {
  let db: DatabaseManager;
  let stateManager: StateManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let coderSessionRepo: CoderSessionRepo;
  let sessionStreamLogRepo: SessionStreamLogRepo;
  let workflowLogRepo: WorkflowLogRepo;
  let server: http.Server;
  let projectId: string;
  let issueId: string;
  let issueNumber: number;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    stateManager = new StateManager(db);

    const projectRepo = stateManager.getProjectRepo();
    const configRepo = stateManager.getConfigRepo();
    const issueRepo = stateManager.getIssueRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();

    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);

    coderSessionRepo = stateManager.getCoderSessionRepo();
    sessionStreamLogRepo = new SessionStreamLogRepo(db);
    workflowLogRepo = stateManager.getWorkflowLogRepo();

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService,
      projectService,
      stateManager,
      null,
      undefined,
      undefined,
      workflowLogRepo,
      sessionStreamLogRepo,
      coderSessionRepo,
    ));
    server = createTestServer(app);
  });

  afterEach(() => {
    server?.close();
    db.close();
  });

  async function setupProjectAndIssue() {
    const project = await projectService.create({ name: 'TestProject', path: '/test/path' });
    projectId = project.id;
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId, title: 'Test Issue' });
    issueId = issue.id;
    issueNumber = issue.number;
    return { project, issue };
  }

  function createSession(issueId: string, overrides: Partial<CoderSession> = {}): CoderSession {
    const session = coderSessionRepo.insert({
      issueId,
      acpSessionId: `acp-${Math.random().toString(36).slice(2)}`,
      model: 'claude-3',
      taskDescription: 'Test task description',
      stage: 'build',
      ...overrides,
    });
    if (overrides.status && overrides.status !== 'running') {
      return coderSessionRepo.updateStatus(session.id, overrides.status);
    }
    return session;
  }

  function insertStreamEvent(acpSessionId: string, issueId: string, eventType: string, data: object, createdAt: string) {
    const id = `evt-${Math.random().toString(36).slice(2)}`;
    const dataStr = JSON.stringify(data);
    db.run(
      `INSERT INTO session_stream_log (id, session_id, issue_id, event_type, data, created_at) VALUES (?, ?, ?, ?, ?, ?)`,
      [id, acpSessionId, issueId, eventType, dataStr, createdAt]
    );
    return { id, sessionId: acpSessionId, issueId, eventType, data: dataStr, createdAt };
  }

  describe('GET /api/issues/:number/coder-sessions', () => {
    it('returns session list without workflowLogs field', async () => {
      const { issue } = await setupProjectAndIssue();
      createSession(issue.id, { model: 'claude-3-5-sonnet', stage: 'build' });

      const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions`);

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data).toHaveLength(1);
      expect(response.body.data[0].id).toBeDefined();
      expect(response.body.data[0].model).toBe('claude-3-5-sonnet');
      expect(response.body.data[0].stage).toBe('build');
      expect(response.body.data[0]).not.toHaveProperty('workflowLogs');
      expect(response.body.data[0]).not.toHaveProperty('turns');
      expect(response.body.data[0]).not.toHaveProperty('metadata');
    });

    it('list endpoint does not load per-session logs', async () => {
      const { issue } = await setupProjectAndIssue();
      const session = createSession(issue.id);

      insertStreamEvent(session.acpSessionId, issue.id, 'agent_message_chunk', {
        content: { text: 'Some response' },
      }, '2024-01-01T10:00:01.000Z');

      insertStreamEvent(session.acpSessionId, issue.id, 'tool_call', {
        toolCallId: 'tc-1',
        toolName: 'Read',
        title: 'src/index.ts',
      }, '2024-01-01T10:00:02.000Z');

      const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions`);

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(1);
      expect(response.body.data[0]).not.toHaveProperty('workflowLogs');
    });

    it('returns multiple sessions ordered by createdAt', async () => {
      const { issue } = await setupProjectAndIssue();
      createSession(issue.id, { acpSessionId: 'sess-1', model: 'claude-3' });
      createSession(issue.id, { acpSessionId: 'sess-2', model: 'gpt-4' });
      createSession(issue.id, { acpSessionId: 'sess-3', model: 'gemini' });

      const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions`);

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(3);
      const models = response.body.data.map((s: any) => s.model);
      expect(models).toEqual(['claude-3', 'gpt-4', 'gemini']);
    });

    it('returns empty array when issue has no sessions', async () => {
      const { issue } = await setupProjectAndIssue();

      const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions`);

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data).toEqual([]);
    });

    it('list response shape matches CoderSessionItem type', async () => {
      const { issue } = await setupProjectAndIssue();
      const session = createSession(issue.id, {
        status: 'completed',
        completedAt: '2024-01-01T12:00:00.000Z',
        title: 'Test Session Title',
        failureReason: null,
        lastDataAt: null,
        probeSentAt: null,
        probeDeadlineAt: null,
      });

      const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions`);

      expect(response.status).toBe(200);
      const item = response.body.data[0];
      expect(item).toHaveProperty('id');
      expect(item).toHaveProperty('acpSessionId');
      expect(item).toHaveProperty('executionId');
      expect(item).toHaveProperty('taskDescription');
      expect(item).toHaveProperty('status');
      expect(item).toHaveProperty('createdAt');
      expect(item).toHaveProperty('completedAt');
      expect(item).toHaveProperty('model');
      expect(item).toHaveProperty('coderType');
      expect(item).toHaveProperty('stage');
      expect(item).toHaveProperty('title');
      expect(item).toHaveProperty('lastDataAt');
      expect(item).toHaveProperty('probeSentAt');
      expect(item).toHaveProperty('probeDeadlineAt');
      expect(item).toHaveProperty('failureReason');
      expect(item).not.toHaveProperty('workflowLogs');
      expect(item).not.toHaveProperty('turns');
      expect(item).not.toHaveProperty('metadata');
    });
  });

  describe('GET /api/issues/:number/coder-sessions/:sessionId', () => {
    it('returns full detail payload including turns for transcript rendering', async () => {
      const { issue } = await setupProjectAndIssue();
      const session = createSession(issue.id, { status: 'completed' });

      insertStreamEvent(session.acpSessionId, issue.id, 'mohist_prompt', {
        role: 'mohist',
        text: 'Implement feature X',
        kind: 'initial',
        sentAt: '2024-01-01T10:00:00.000Z',
      }, '2024-01-01T10:00:00.000Z');

      insertStreamEvent(session.acpSessionId, issue.id, 'agent_message_chunk', {
        content: { text: 'Response to prompt' },
      }, '2024-01-01T10:00:01.000Z');

      const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions/${session.id}`);

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data).toHaveProperty('id', session.id);
      expect(response.body.data).toHaveProperty('turns');
      expect(response.body.data).toHaveProperty('metadata');
      expect(response.body.data.turns).toHaveLength(1);
      expect(response.body.data.turns[0].assistant).toHaveLength(1);
      expect(response.body.data.turns[0].assistant[0].type).toBe('text');
    });

    it('detail endpoint includes metadata for session inspection', async () => {
      const { issue } = await setupProjectAndIssue();
      const session = createSession(issue.id, { status: 'completed', model: 'claude-3-5-sonnet', stage: 'build' });

      insertStreamEvent(session.acpSessionId, issue.id, 'mohist_prompt', {
        role: 'mohist',
        text: 'Task prompt',
        kind: 'task',
        sentAt: '2024-01-01T10:00:00.000Z',
      }, '2024-01-01T10:00:00.000Z');

      insertStreamEvent(session.acpSessionId, issue.id, 'agent_message_chunk', {
        content: { text: 'Response' },
      }, '2024-01-01T10:00:01.000Z');

      const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions/${session.id}`);

      expect(response.status).toBe(200);
      expect(response.body.data.metadata).toBeDefined();
      expect(response.body.data.metadata.model).toBe('claude-3-5-sonnet');
      expect(response.body.data.metadata.stage).toBe('build');
      expect(response.body.data.metadata.sessionId).toBe(session.id);
    });

    it('detail endpoint returns workflowLogs when falling back to legacy logs', async () => {
      const { issue } = await setupProjectAndIssue();
      const session = createSession(issue.id, { status: 'completed' });

      const id = `wl-${Math.random().toString(36).slice(2)}`;
      db.run(
        `INSERT INTO workflow_log (id, issue_id, session_id, event_type, data, created_at) VALUES (?, ?, ?, ?, ?, ?)`,
        [id, issue.id, session.acpSessionId, 'mohist_prompt', JSON.stringify({ role: 'mohist', text: 'Legacy prompt', kind: 'task', sentAt: '2024-01-01T10:00:00.000Z' }), '2024-01-01T10:00:00.000Z']
      );

      const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions/${session.id}`);

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveProperty('workflowLogs');
      expect(response.body.data.incomplete).toBe(false);
    });

    it('detail endpoint returns 404 for non-existent session', async () => {
      const { issue } = await setupProjectAndIssue();

      const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions/non-existent-id`);

      expect(response.status).toBe(404);
      expect(response.body.success).toBe(false);
    });

    it('detail endpoint returns 404 when session belongs to different issue', async () => {
      const { issue: issue1 } = await setupProjectAndIssue();
      const session = createSession(issue1.id);

      const project2 = await projectService.create({ name: 'TestProject2', path: '/test/path2' });
      projectService.setCurrent(project2);
      const issue2 = await issueService.create({ projectId: project2.id, title: 'Other Issue' });

      const response = await request(server).get(`/api/issues/${issue2.number}/coder-sessions/${session.id}`);

      expect(response.status).toBe(404);
      expect(response.body.success).toBe(false);
    });
  });

  describe('List vs Detail separation', () => {
    it('list endpoint is significantly faster than detail for sessions with logs', async () => {
      const { issue } = await setupProjectAndIssue();
      const session = createSession(issue.id, { status: 'completed' });

      for (let i = 0; i < 20; i++) {
        insertStreamEvent(session.acpSessionId, issue.id, 'mohist_prompt', {
          role: 'mohist',
          text: `Prompt ${i}`,
          kind: 'task',
          sentAt: `2024-01-01T${String(i).padStart(2, '0')}:00:00.000Z`,
        }, `2024-01-01T${String(i).padStart(2, '0')}:00:00.000Z`);
        insertStreamEvent(session.acpSessionId, issue.id, 'agent_message_chunk', {
          content: { text: `Response ${i}` },
        }, `2024-01-01T${String(i).padStart(2, '0')}:00:01.000Z`);
      }

      const listStart = Date.now();
      const listResponse = await request(server).get(`/api/issues/${issue.number}/coder-sessions`);
      const listTime = Date.now() - listStart;

      const detailStart = Date.now();
      const detailResponse = await request(server).get(`/api/issues/${issue.number}/coder-sessions/${session.id}`);
      const detailTime = Date.now() - detailStart;

      expect(listResponse.status).toBe(200);
      expect(detailResponse.status).toBe(200);
      expect(listTime).toBeLessThan(detailTime);
    });
  });
});

describe('findBySessionIds ordering for mixed precision timestamps', () => {
  let db: DatabaseManager;
  let sessionStreamLogRepo: SessionStreamLogRepo;
  let workflowLogRepo: WorkflowLogRepo;
  let issueId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    const projectRepo = new ProjectRepo(db);
    const proj = projectRepo.create({ name: 'Test', path: '/test' });
    const issueRepo = new IssueRepo(db);
    issueRepo.create({ number: 1, projectId: proj.id, title: 'Test Issue' });
    issueId = db.get<{ id: string }>('SELECT id FROM issues WHERE project_id = ?', [proj.id])!.id;

    sessionStreamLogRepo = new SessionStreamLogRepo(db);
    workflowLogRepo = new WorkflowLogRepo(db);
  });

  afterEach(() => {
    db.close();
  });

  function insertStreamLog(sessionId: string, eventType: string, data: object, createdAt: string) {
    const id = `ssl-${Math.random().toString(36).slice(2)}`;
    db.run(
      `INSERT INTO session_stream_log (id, session_id, issue_id, event_type, data, created_at) VALUES (?, ?, ?, ?, ?, ?)`,
      [id, sessionId, issueId, eventType, JSON.stringify(data), createdAt]
    );
  }

  function insertWorkflowLog(sessionId: string | null, eventType: string, data: object, createdAt: string) {
    const id = `wl-${Math.random().toString(36).slice(2)}`;
    db.run(
      `INSERT INTO workflow_log (id, issue_id, session_id, event_type, data, created_at) VALUES (?, ?, ?, ?, ?, ?)`,
      [id, issueId, sessionId, eventType, JSON.stringify(data), createdAt]
    );
  }

  describe('SessionStreamLogRepo.findBySessionIds', () => {
    it('returns rows grouped by session_id ordered by created_at, rowid', () => {
      insertStreamLog('sess-1', 'agent_message_chunk', { text: 'first' }, '2024-01-01T10:00:00.000Z');
      insertStreamLog('sess-2', 'agent_message_chunk', { text: 'second' }, '2024-01-01T10:00:01.000Z');
      insertStreamLog('sess-1', 'tool_call', { toolCallId: 'tc-1' }, '2024-01-01T10:00:02.000Z');
      insertStreamLog('sess-2', 'tool_call', { toolCallId: 'tc-2' }, '2024-01-01T10:00:03.000Z');

      const results = sessionStreamLogRepo.findBySessionIds(['sess-1', 'sess-2']);

      expect(results).toHaveLength(4);
      expect(results[0].sessionId).toBe('sess-1');
      expect(results[1].sessionId).toBe('sess-1');
      expect(results[2].sessionId).toBe('sess-2');
      expect(results[3].sessionId).toBe('sess-2');
      expect(results[0].eventType).toBe('agent_message_chunk');
      expect(results[1].eventType).toBe('tool_call');
    });

    it('handles mixed millisecond and second-precision timestamps', () => {
      insertStreamLog('sess-1', 'agent_message_chunk', { text: 'ms-1' }, '2024-01-01T10:00:00.500Z');
      insertStreamLog('sess-1', 'agent_message_chunk', { text: 'sec-1' }, '2024-01-01T10:00:00.000Z');
      insertStreamLog('sess-1', 'agent_message_chunk', { text: 'ms-2' }, '2024-01-01T10:00:01.000Z');

      const results = sessionStreamLogRepo.findBySessionIds(['sess-1']);

      expect(results).toHaveLength(3);
      expect(results[0].sessionId).toBe('sess-1');
      expect(results[1].sessionId).toBe('sess-1');
      expect(results[2].sessionId).toBe('sess-1');
    });

    it('returns empty array for empty input', () => {
      const results = sessionStreamLogRepo.findBySessionIds([]);
      expect(results).toEqual([]);
    });

    it('returns empty array for non-existent session ids', () => {
      insertStreamLog('sess-1', 'agent_message_chunk', { text: 'test' }, '2024-01-01T10:00:00.000Z');

      const results = sessionStreamLogRepo.findBySessionIds(['non-existent']);

      expect(results).toEqual([]);
    });

    it('orders by session_id, created_at, rowid for deterministic fallback', () => {
      db.run(`INSERT INTO session_stream_log (id, session_id, issue_id, event_type, data, created_at) VALUES (?, ?, ?, ?, ?, ?)`,
        ['row-a', 'sess-1', issueId, 'agent_message_chunk', '{"text":"a"}', '2024-01-01T10:00:00']);
      db.run(`INSERT INTO session_stream_log (id, session_id, issue_id, event_type, data, created_at) VALUES (?, ?, ?, ?, ?, ?)`,
        ['row-b', 'sess-1', issueId, 'agent_message_chunk', '{"text":"b"}', '2024-01-01T10:00:00']);

      const results = sessionStreamLogRepo.findBySessionIds(['sess-1']);

      expect(results).toHaveLength(2);
      expect(results[0].id).toBe('row-a');
      expect(results[1].id).toBe('row-b');
    });
  });

  describe('WorkflowLogRepo.findBySessionIds', () => {
    it('returns rows grouped by session_id ordered by created_at, rowid', () => {
      insertWorkflowLog('sess-1', 'tool_call', { toolCallId: 'tc-1' }, '2024-01-01T10:00:00.000Z');
      insertWorkflowLog('sess-2', 'tool_call', { toolCallId: 'tc-2' }, '2024-01-01T10:00:01.000Z');
      insertWorkflowLog('sess-1', 'tool_call_update', { toolCallId: 'tc-1', status: 'completed' }, '2024-01-01T10:00:02.000Z');

      const results = workflowLogRepo.findBySessionIds(['sess-1', 'sess-2']);

      expect(results).toHaveLength(3);
      expect(results[0].sessionId).toBe('sess-1');
      expect(results[1].sessionId).toBe('sess-1');
      expect(results[2].sessionId).toBe('sess-2');
    });

    it('handles mixed millisecond and second-precision timestamps', () => {
      insertWorkflowLog('sess-1', 'tool_call', { toolCallId: 'tc-1' }, '2024-01-01T10:00:00.500Z');
      insertWorkflowLog('sess-1', 'tool_call', { toolCallId: 'tc-2' }, '2024-01-01T10:00:00.000Z');
      insertWorkflowLog('sess-1', 'tool_call_update', { toolCallId: 'tc-1', status: 'completed' }, '2024-01-01T10:00:01.000Z');

      const results = workflowLogRepo.findBySessionIds(['sess-1']);

      expect(results).toHaveLength(3);
    });

    it('returns empty array for empty input', () => {
      const results = workflowLogRepo.findBySessionIds([]);
      expect(results).toEqual([]);
    });

    it('handles null session_id in workflow_log', () => {
      insertWorkflowLog(null, 'some_event', { data: 'test' }, '2024-01-01T10:00:00.000Z');

      const results = workflowLogRepo.findBySessionIds(['non-existent']);

      expect(results).toHaveLength(0);
    });
  });
});

describe('High session count performance', () => {
  let db: DatabaseManager;
  let stateManager: StateManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let coderSessionRepo: CoderSessionRepo;
  let sessionStreamLogRepo: SessionStreamLogRepo;
  let workflowLogRepo: WorkflowLogRepo;
  let server: http.Server;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    stateManager = new StateManager(db);

    const projectRepo = stateManager.getProjectRepo();
    const configRepo = stateManager.getConfigRepo();
    const issueRepo = stateManager.getIssueRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();

    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);

    coderSessionRepo = stateManager.getCoderSessionRepo();
    sessionStreamLogRepo = new SessionStreamLogRepo(db);
    workflowLogRepo = stateManager.getWorkflowLogRepo();

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService,
      projectService,
      stateManager,
      null,
      undefined,
      undefined,
      workflowLogRepo,
      sessionStreamLogRepo,
      coderSessionRepo,
    ));
    server = createTestServer(app);
  });

  afterEach(() => {
    server?.close();
    db.close();
  });

  it('list endpoint responds in under 1 second for 50+ sessions', async () => {
    const project = await projectService.create({ name: 'TestProject', path: '/test/path' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'High Session Count Issue' });
    const issueNumber = issue.number;

    for (let i = 0; i < 55; i++) {
      coderSessionRepo.insert({
        issueId: issue.id,
        acpSessionId: `acp-large-${i}`,
        model: 'claude-3',
        taskDescription: `Task description ${i}`,
        stage: 'build',
      });
    }

    const start = Date.now();
    const response = await request(server).get(`/api/issues/${issueNumber}/coder-sessions`);
    const elapsed = Date.now() - start;

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);
    expect(response.body.data).toHaveLength(55);
    expect(elapsed).toBeLessThan(1000);
  });

  it('list endpoint does not O(N) query logs for each session', async () => {
    const project = await projectService.create({ name: 'TestProject', path: '/test/path' });
    projectService.setCurrent(project);
    const issue = await issueService.create({ projectId: project.id, title: 'Multi Session Issue' });
    const issueNumber = issue.number;

    for (let i = 0; i < 20; i++) {
      const session = coderSessionRepo.insert({
        issueId: issue.id,
        acpSessionId: `acp-multi-${i}`,
        model: 'claude-3',
        taskDescription: `Task ${i}`,
        stage: 'build',
      });
      for (let j = 0; j < 5; j++) {
        sessionStreamLogRepo.insert(issue.id, session.acpSessionId, 'agent_message_chunk', { text: `msg-${j}` });
      }
    }

    const start = Date.now();
    const response = await request(server).get(`/api/issues/${issueNumber}/coder-sessions`);
    const elapsed = Date.now() - start;

    expect(response.status).toBe(200);
    expect(response.body.data).toHaveLength(20);
    expect(elapsed).toBeLessThan(500);
    expect(response.body.data[0]).not.toHaveProperty('workflowLogs');
  });
});