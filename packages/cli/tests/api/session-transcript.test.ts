import { describe, it, expect, beforeEach } from 'vitest';
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

describe('Session Transcript API', () => {
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

  describe('GET /api/issues/:number/coder-sessions/:sessionId', () => {
    describe('transcript assembly', () => {
      it('returns structured transcript data for completed sessions without SSE state', async () => {
        const { issue } = await setupProjectAndIssue();
        const session = createSession(issue.id, { status: 'completed' });
        const acpSessionId = session.acpSessionId;

        insertStreamEvent(session.acpSessionId, issue.id, 'mohist_prompt', {
          role: 'mohist',
          text: 'Implement feature X',
          kind: 'initial',
          sentAt: '2024-01-01T10:00:00.000Z',
        }, '2024-01-01T10:00:00.000Z');

        insertStreamEvent(session.acpSessionId, issue.id, 'agent_message_chunk', {
          content: { text: 'I will implement feature X now.' },
        }, '2024-01-01T10:00:01.000Z');

        insertStreamEvent(session.acpSessionId, issue.id, 'tool_call', {
          toolCallId: 'tc-1',
          toolName: 'Read',
          title: 'src/index.ts',
          input: '{"file_path":"src/index.ts"}',
        }, '2024-01-01T10:00:02.000Z');

        insertStreamEvent(session.acpSessionId, issue.id, 'tool_call_update', {
          toolCallId: 'tc-1',
          status: 'completed',
          output: 'file contents',
        }, '2024-01-01T10:00:03.000Z');

        const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions/${session.id}`);

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.turns).toHaveLength(1);
        expect(response.body.data.turns[0].user.role).toBe('mohist');
        expect(response.body.data.turns[0].user.text).toBe('Implement feature X');
        expect(response.body.data.turns[0].user.kind).toBe('initial');
        expect(response.body.data.turns[0].assistant).toHaveLength(2);
        expect(response.body.data.turns[0].assistant[0].type).toBe('text');
        expect(response.body.data.turns[0].assistant[1].type).toBe('tool');
        expect(response.body.data.incomplete).toBe(false);
      });

      it('returns Mohist prompts even when no ACP user_message_chunk exists', async () => {
        const { issue } = await setupProjectAndIssue();
        const session = createSession(issue.id, { status: 'running' });
        const acpSessionId = session.acpSessionId;

        insertStreamEvent(session.acpSessionId, issue.id, 'mohist_prompt', {
          role: 'mohist',
          text: 'First prompt without ACP chunk',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        }, '2024-01-01T10:00:00.000Z');

        insertStreamEvent(session.acpSessionId, issue.id, 'agent_message_chunk', {
          content: { text: 'Response to first prompt' },
        }, '2024-01-01T10:00:01.000Z');

        const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions/${session.id}`);

        expect(response.status).toBe(200);
        expect(response.body.data.turns).toHaveLength(1);
        expect(response.body.data.turns[0].user.text).toBe('First prompt without ACP chunk');
        expect(response.body.data.turns[0].assistant[0].type).toBe('text');
      });

      it('creates new turns for retry/follow-up prompts', async () => {
        const { issue } = await setupProjectAndIssue();
        const session = createSession(issue.id, { status: 'running' });

        insertStreamEvent(session.acpSessionId, issue.id, 'mohist_prompt', {
          role: 'mohist',
          text: 'Initial task',
          kind: 'initial',
          sentAt: '2024-01-01T10:00:00.000Z',
        }, '2024-01-01T10:00:00.000Z');

        insertStreamEvent(session.acpSessionId, issue.id, 'agent_message_chunk', {
          content: { text: 'Initial response' },
        }, '2024-01-01T10:00:01.000Z');

        insertStreamEvent(session.acpSessionId, issue.id, 'mohist_prompt', {
          role: 'mohist',
          text: 'Retry prompt',
          kind: 'retry',
          sentAt: '2024-01-01T10:01:00.000Z',
        }, '2024-01-01T10:01:00.000Z');

        insertStreamEvent(session.acpSessionId, issue.id, 'agent_message_chunk', {
          content: { text: 'Retry response' },
        }, '2024-01-01T10:01:01.000Z');

        const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions/${session.id}`);

        expect(response.status).toBe(200);
        expect(response.body.data.turns).toHaveLength(2);
        expect(response.body.data.turns[0].user.kind).toBe('initial');
        expect(response.body.data.turns[0].completedAt).toBe('2024-01-01T10:01:00.000Z');
        expect(response.body.data.turns[1].user.kind).toBe('retry');
        expect(response.body.data.turns[1].completedAt).toBeNull();
      });

      it('terminal session statuses close open turns', async () => {
        const { issue } = await setupProjectAndIssue();
        const session = createSession(issue.id, { status: 'completed' });

        insertStreamEvent(session.acpSessionId, issue.id, 'mohist_prompt', {
          role: 'mohist',
          text: 'Task prompt',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        }, '2024-01-01T10:00:00.000Z');

        insertStreamEvent(session.acpSessionId, issue.id, 'agent_message_chunk', {
          content: { text: 'Response text' },
        }, '2024-01-01T10:00:01.000Z');

        const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions/${session.id}`);

        expect(response.status).toBe(200);
        expect(response.body.data.turns[0].completedAt).toBeTruthy();
      });
    });

    describe('legacy fallback', () => {
      it('returns incomplete synthetic turn for legacy sessions without prompts', async () => {
        const { issue } = await setupProjectAndIssue();
        const session = createSession(issue.id, { status: 'completed' });

        insertStreamEvent(session.acpSessionId, issue.id, 'agent_message_chunk', {
          content: { text: 'Legacy response without prompt' },
        }, '2024-01-01T10:00:01.000Z');

        insertStreamEvent(session.acpSessionId, issue.id, 'tool_call', {
          toolCallId: 'tc-1',
          toolName: 'Bash',
          input: '{"command":"ls"}',
        }, '2024-01-01T10:00:02.000Z');

        const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions/${session.id}`);

        expect(response.status).toBe(200);
        expect(response.body.data.turns).toHaveLength(1);
        expect(response.body.data.turns[0].user.kind).toBe('legacy-missing');
        expect(response.body.data.turns[0].user.text).toBe('Prompt was not recorded for this historical session');
        expect(response.body.data.turns[0].incomplete).toBe(true);
        expect(response.body.data.incomplete).toBe(true);
      });

      it('returns workflowLogs fallback when session_stream_log is empty', async () => {
        const { issue } = await setupProjectAndIssue();
        const session = createSession(issue.id, { status: 'completed' });

        workflowLogRepo.insert(issue.id, session.acpSessionId, 'agent_message_chunk', { content: { text: 'Fallback text' } });

        const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions/${session.id}`);

        expect(response.status).toBe(200);
        expect(response.body.data.turns).toHaveLength(1);
        expect(response.body.data.workflowLogs).toBeDefined();
        const assistantText = response.body.data.turns[0].assistant
          .map((p: any) => p.text ?? '')
          .join('');
        expect(assistantText).toContain('Fallback text');
      });
    });

    describe('metadata', () => {
      it('includes session metadata with correct structure', async () => {
        const { issue } = await setupProjectAndIssue();
        const session = createSession(issue.id, {
          status: 'completed',
          model: 'claude-3-opus',
          stage: 'build',
          title: 'Test Session Title',
        });

        const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions/${session.id}`);

        expect(response.status).toBe(200);
        expect(response.body.data.metadata).toBeDefined();
        expect(response.body.data.metadata.sessionId).toBe(session.id);
        expect(response.body.data.metadata.issueId).toBe(issue.id);
        expect(response.body.data.metadata.status).toBe('completed');
        expect(response.body.data.metadata.model).toBe('claude-3-opus');
        expect(response.body.data.metadata.stage).toBe('build');
        expect(response.body.data.metadata.title).toBe('Test Session Title');
      });

      it('completedAt is null for running sessions', async () => {
        const { issue } = await setupProjectAndIssue();
        const session = createSession(issue.id, { status: 'running' });

        insertStreamEvent(session.acpSessionId, issue.id, 'mohist_prompt', {
          role: 'mohist',
          text: 'Running task',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        }, '2024-01-01T10:00:00.000Z');

        const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions/${session.id}`);

        expect(response.status).toBe(200);
        expect(response.body.data.metadata.completedAt).toBeNull();
      });

      it('completedAt is set for terminal sessions', async () => {
        const { issue } = await setupProjectAndIssue();
        const session = createSession(issue.id, { status: 'completed' });

        insertStreamEvent(session.acpSessionId, issue.id, 'mohist_prompt', {
          role: 'mohist',
          text: 'Task',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        }, '2024-01-01T10:00:00.000Z');

        const response = await request(server).get(`/api/issues/${issue.number}/coder-sessions/${session.id}`);

        expect(response.status).toBe(200);
        expect(response.body.data.metadata.completedAt).not.toBeNull();
      });
    });
  });
});