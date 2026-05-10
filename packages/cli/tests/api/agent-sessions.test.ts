import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import http from 'node:http';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../../src/db/database';
import { StateManager } from '../../src/server/state-manager';
import { ProjectService } from '../../src/services/project-service';
import { IssueService } from '../../src/services/issue-service';
import { EventBus, AgentRunnerService } from '../../src/services';
import { CoderSessionRepo } from '../../src/db/coder-session-repo';
import { WorkflowLogRepo } from '../../src/db/workflow-log-repo';
import { createAgentRoutes } from '../../src/api/agent';

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

describe('GET /api/agent/sessions', () => {
  let db: DatabaseManager;
  let stateManager: StateManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let coderSessionRepo: CoderSessionRepo;
  let workflowLogRepo: WorkflowLogRepo;
  let server: http.Server;
  let projectId: string;

  beforeEach(async () => {
    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);

    const projectRepo = stateManager.getProjectRepo();
    const configRepo = stateManager.getConfigRepo();
    const issueRepo = stateManager.getIssueRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();

    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);
    coderSessionRepo = stateManager.getCoderSessionRepo();
    workflowLogRepo = stateManager.getWorkflowLogRepo();

    const project = await projectService.create({ name: 'TestProject', path: '/test/path' });
    projectId = project.id;
    projectService.setCurrent(project);

    const eventBus = new EventBus();
    const agentRunner = new AgentRunnerService(eventBus);

    const app = new Hono();
    app.route('/api/agent', createAgentRoutes(agentRunner, coderSessionRepo, projectService));
    server = createTestServer(app);
  });

  afterEach(() => {
    server?.close();
    db.close();
  });

  function createIssue(title: string) {
    return issueService.create({ projectId, title });
  }

  function createSession(issueId: string, opts: {
    status?: string;
    model?: string;
    taskDescription?: string;
    stage?: string;
    acpSessionId?: string;
  } = {}) {
    const session = coderSessionRepo.insert({
      issueId,
      acpSessionId: opts.acpSessionId ?? `acp-${Math.random().toString(36).slice(2)}`,
      model: opts.model,
      taskDescription: opts.taskDescription ?? null,
      stage: opts.stage,
    });
    if (opts.status && opts.status !== 'running') {
      coderSessionRepo.updateStatus(session.id, opts.status);
    }
    return session;
  }

  function insertWorkflowLog(issueId: string, sessionId: string, createdAt?: string) {
    const id = `wl-${Math.random().toString(36).slice(2)}`;
    const ts = createdAt ?? new Date().toISOString();
    db.run(
      `INSERT INTO workflow_log (id, issue_id, session_id, event_type, data, created_at) VALUES (?, ?, ?, 'test_event', '{}', ?)`,
      [id, issueId, sessionId, ts],
    );
    return id;
  }

  it('returns sessions with correct fields', async () => {
    const issue = createIssue('Fix login bug');
    const session = createSession(issue.id, {
      model: 'gpt-4',
      taskDescription: 'Implement login API endpoint',
      stage: 'build',
      acpSessionId: 'acp-001',
    });

    const response = await request(server).get('/api/agent/sessions');

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);
    expect(response.body.data).toHaveLength(1);

    const s = response.body.data[0];
    expect(s.issueNumber).toBe(issue.number);
    expect(s.issueTitle).toBe('Fix login bug');
    expect(s.issueStage).toBe('backlog');
    expect(s.sessionId).toBe(session.id);
    expect(s.status).toBe('running');
    expect(s.model).toBe('gpt-4');
    expect(s.taskDescription).toBe('Implement login API endpoint');
    expect(s.createdAt).toBeDefined();
    expect(s.completedAt).toBeNull();
    expect(s.lastActivityAt).toBeNull();
  });

  it('filters by status=running', async () => {
    const issue1 = createIssue('Running issue');
    const issue2 = createIssue('Completed issue');
    createSession(issue1.id, { status: 'running' });
    createSession(issue2.id, { status: 'completed' });

    const response = await request(server).get('/api/agent/sessions?status=running');

    expect(response.status).toBe(200);
    expect(response.body.data).toHaveLength(1);
    expect(response.body.data[0].status).toBe('running');
  });

  it('filters by status=completed', async () => {
    const issue1 = createIssue('Running');
    const issue2 = createIssue('Completed');
    createSession(issue1.id, { status: 'running' });
    createSession(issue2.id, { status: 'completed' });

    const response = await request(server).get('/api/agent/sessions?status=completed');

    expect(response.status).toBe(200);
    expect(response.body.data).toHaveLength(1);
    expect(response.body.data[0].status).toBe('completed');
  });

  it('filters by status=failed', async () => {
    const issue1 = createIssue('Failed');
    const issue2 = createIssue('Running');
    createSession(issue1.id, { status: 'failed' });
    createSession(issue2.id, { status: 'running' });

    const response = await request(server).get('/api/agent/sessions?status=failed');

    expect(response.status).toBe(200);
    expect(response.body.data).toHaveLength(1);
    expect(response.body.data[0].status).toBe('failed');
  });

  it('limits results with ?limit=10', async () => {
    for (let i = 0; i < 15; i++) {
      const issue = createIssue(`Issue ${i}`);
      createSession(issue.id, { status: 'running' });
    }

    const response = await request(server).get('/api/agent/sessions?limit=10');

    expect(response.status).toBe(200);
    expect(response.body.data).toHaveLength(10);
  });

  it('defaults limit to 50', async () => {
    for (let i = 0; i < 55; i++) {
      const issue = createIssue(`Issue ${i}`);
      createSession(issue.id, { status: 'running' });
    }

    const response = await request(server).get('/api/agent/sessions');

    expect(response.status).toBe(200);
    expect(response.body.data).toHaveLength(50);
  });

  it('derives lastActivityAt from workflow_log', async () => {
    const issue = createIssue('With activity');
    const session = createSession(issue.id, { acpSessionId: 'acp-activity' });

    const ts1 = '2026-01-01T10:00:00.000Z';
    const ts2 = '2026-01-01T10:05:00.000Z';
    insertWorkflowLog(issue.id, 'acp-activity', ts1);
    insertWorkflowLog(issue.id, 'acp-activity', ts2);

    const response = await request(server).get('/api/agent/sessions');

    expect(response.status).toBe(200);
    expect(response.body.data).toHaveLength(1);
    expect(response.body.data[0].lastActivityAt).toBe(ts2);
  });

  it('returns null lastActivityAt when no workflow_log entries', async () => {
    const issue = createIssue('No activity');
    createSession(issue.id);

    const response = await request(server).get('/api/agent/sessions');

    expect(response.status).toBe(200);
    expect(response.body.data[0].lastActivityAt).toBeNull();
  });

  it('returns empty array when no sessions exist', async () => {
    const response = await request(server).get('/api/agent/sessions');

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);
    expect(response.body.data).toEqual([]);
  });

  it('combines status filter and limit', async () => {
    for (let i = 0; i < 8; i++) {
      const issue = createIssue(`Running ${i}`);
      createSession(issue.id, { status: 'running' });
    }
    for (let i = 0; i < 3; i++) {
      const issue = createIssue(`Completed ${i}`);
      createSession(issue.id, { status: 'completed' });
    }

    const response = await request(server).get('/api/agent/sessions?status=running&limit=5');

    expect(response.status).toBe(200);
    expect(response.body.data).toHaveLength(5);
    for (const s of response.body.data) {
      expect(s.status).toBe('running');
    }
  });

  it('orders results by createdAt descending', async () => {
    const issue1 = createIssue('First');
    const issue2 = createIssue('Second');
    const issue3 = createIssue('Third');

    createSession(issue1.id, { acpSessionId: 's1' });
    createSession(issue2.id, { acpSessionId: 's2' });
    createSession(issue3.id, { acpSessionId: 's3' });

    const response = await request(server).get('/api/agent/sessions');

    expect(response.status).toBe(200);
    const sessions = response.body.data;
    const dates = sessions.map((s: any) => new Date(s.createdAt).getTime());
    for (let i = 1; i < dates.length; i++) {
      expect(dates[i - 1]).toBeGreaterThanOrEqual(dates[i]);
    }
  });

  it('returns completedAt for completed sessions', async () => {
    const issue = createIssue('Done issue');
    createSession(issue.id, { status: 'completed' });

    const response = await request(server).get('/api/agent/sessions');

    expect(response.status).toBe(200);
    expect(response.body.data[0].completedAt).toBeDefined();
    expect(response.body.data[0].completedAt).not.toBeNull();
  });

  it('truncates taskDescription to 200 characters', async () => {
    const issue = createIssue('Long desc');
    const longDesc = 'A'.repeat(300);
    createSession(issue.id, { taskDescription: longDesc });

    const response = await request(server).get('/api/agent/sessions');

    expect(response.status).toBe(200);
    expect(response.body.data[0].taskDescription).toHaveLength(200);
  });

  it('returns empty array when no current project', async () => {
    projectService.clearCurrent();

    const response = await request(server).get('/api/agent/sessions');

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);
    expect(response.body.data).toEqual([]);
  });

  it('returns Running for the latest running session on /session-status', async () => {
    const issue = createIssue('Running issue');
    const session = createSession(issue.id, { status: 'running', acpSessionId: 'acp-running' });

    const response = await request(server).get('/api/agent/session-status');

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);
    expect(response.body.data.sessionId).toBe(session.id);
    expect(response.body.data.status).toBe('running');
    expect(response.body.data.currentSessionState).toBe('Running');
  });

  it('returns Checking session for a probing session on /session-status', async () => {
    const issue = createIssue('Probing issue');
    const session = createSession(issue.id, { acpSessionId: 'acp-probing' });
    const probeSentAt = '2026-05-10T10:00:00.000Z';
    const probeDeadlineAt = '2026-05-10T10:00:30.000Z';
    coderSessionRepo.markProbing(session.id, probeSentAt, probeDeadlineAt);

    const response = await request(server).get('/api/agent/session-status');

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);
    expect(response.body.data.sessionId).toBe(session.id);
    expect(response.body.data.status).toBe('probing');
    expect(response.body.data.currentSessionState).toBe('Checking session');
    expect(response.body.data.probeSentAt).toBe(probeSentAt);
    expect(response.body.data.probeDeadlineAt).toBe(probeDeadlineAt);
  });

  it('returns Session failed for a failed session on /session-status', async () => {
    const issue = createIssue('Failed issue');
    const session = createSession(issue.id, { acpSessionId: 'acp-failed' });
    coderSessionRepo.markFailed(session.id, 'probe_timeout');

    const response = await request(server).get('/api/agent/session-status');

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);
    expect(response.body.data.sessionId).toBe(session.id);
    expect(response.body.data.status).toBe('failed');
    expect(response.body.data.currentSessionState).toBe('Session failed');
    expect(response.body.data.failureReason).toBe('probe_timeout');
  });

  it('returns No active session on /session-status when only completed sessions exist', async () => {
    const issue = createIssue('Completed issue');
    createSession(issue.id, { status: 'completed', acpSessionId: 'acp-completed' });

    const response = await request(server).get('/api/agent/session-status');

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);
    expect(response.body.data).toEqual({
      sessionId: null,
      acpSessionId: null,
      status: null,
      currentSessionState: 'No active session',
      lastDataAt: null,
      probeSentAt: null,
      probeDeadlineAt: null,
      failureReason: null,
    });
  });

  it('returns No active session on /session-status for a stale failed session on an inactive issue', async () => {
    const issue = createIssue('Stale failed issue');
    stateManager.getIssueRepo().update(issue.id, { status: 'done' as any });

    const session = createSession(issue.id, { acpSessionId: 'acp-stale-failed' });
    coderSessionRepo.markFailed(session.id, 'probe_timeout');

    const response = await request(server).get('/api/agent/session-status');

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);
    expect(response.body.data).toEqual({
      sessionId: null,
      acpSessionId: null,
      status: null,
      currentSessionState: 'No active session',
      lastDataAt: null,
      probeSentAt: null,
      probeDeadlineAt: null,
      failureReason: null,
    });
  });

  it('returns No active session on /session-status when no current project', async () => {
    projectService.clearCurrent();

    const response = await request(server).get('/api/agent/session-status');

    expect(response.status).toBe(200);
    expect(response.body.success).toBe(true);
    expect(response.body.data).toEqual({
      sessionId: null,
      acpSessionId: null,
      status: null,
      currentSessionState: 'No active session',
      lastDataAt: null,
      probeSentAt: null,
      probeDeadlineAt: null,
      failureReason: null,
    });
  });
});
