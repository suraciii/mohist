import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import http from 'node:http';
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
import { Stage, IssueStatus, Priority, VALID_PRIORITIES, normalizePriority } from '../src/types';
import { ingestBody, BodyIngestResult } from '../src/cli/commands/issue';

vi.mock('../src/cli/server-check', () => ({ requireServer: vi.fn().mockResolvedValue(undefined) }));
vi.mock('../src/cli/api-client', () => ({ apiClient: vi.fn() }));

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

describe('Body Ingest - CLI Helper', () => {
  const tmpDir = os.tmpdir();

  function tmpFile(name: string, content: string): string {
    const p = path.join(tmpDir, `mohist-body-test-${name}`);
    fs.writeFileSync(p, content, 'utf-8');
    return p;
  }

  afterEach(() => {
    for (const f of fs.readdirSync(tmpDir).filter(f => f.startsWith('mohist-body-test-'))) {
      try { fs.unlinkSync(path.join(tmpDir, f)); } catch {}
    }
  });

  describe('ingestBody with @file reference', () => {
    it('reads file contents when body starts with @', async () => {
      const filePath = tmpFile('body.md', '# Hello\n\nWorld');
      const result = await ingestBody(`@${filePath}`, undefined);
      expect(result.error).toBeNull();
      expect(result.body).toBe('# Hello\n\nWorld');
    });

    it('returns error when referenced file does not exist', async () => {
      const result = await ingestBody('@/nonexistent/file.md', undefined);
      expect(result.error).not.toBeNull();
      expect(result.body).toBeUndefined();
    });
  });

  describe('ingestBody with --body-file', () => {
    it('reads file contents from --body-file', async () => {
      const filePath = tmpFile('explicit-body.md', 'Explicit body content');
      const result = await ingestBody(undefined, filePath);
      expect(result.error).toBeNull();
      expect(result.body).toBe('Explicit body content');
    });

    it('returns error when --body-file does not exist', async () => {
      const result = await ingestBody(undefined, '/nonexistent/file.md');
      expect(result.error).not.toBeNull();
    });
  });

  describe('ingestBody with stdin (-)', () => {
    it('returns a promise when body is "-"', async () => {
      const result = ingestBody('-', undefined);
      expect(typeof (result as any).then).toBe('function');
    });
  });

  describe('ingestBody with literal string', () => {
    it('returns the literal string unchanged when no @ or - prefix', async () => {
      const result = await ingestBody('plain literal body', undefined);
      expect(result.error).toBeNull();
      expect(result.body).toBe('plain literal body');
    });
  });

  describe('ingestBody mutual exclusion', () => {
    it('returns error when both --body and --body-file are provided', async () => {
      const filePath = tmpFile('a.md', 'a');
      const result = await ingestBody('@' + filePath, filePath);
      expect(result.error).not.toBeNull();
      expect(result.error).toContain('--body');
      expect(result.error).toContain('--body-file');
    });
  });
});

describe('Priority Normalization - CLI Helper', () => {
  it('normalizes uppercase P0-P4 to lowercase', () => {
    expect(normalizePriority('P0')).toBe('p0');
    expect(normalizePriority('P1')).toBe('p1');
    expect(normalizePriority('P2')).toBe('p2');
    expect(normalizePriority('P3')).toBe('p3');
    expect(normalizePriority('P4')).toBe('p4');
  });

  it('normalizes lowercase p0-p4 unchanged', () => {
    for (const p of VALID_PRIORITIES) {
      expect(normalizePriority(p)).toBe(p);
    }
  });

  it('returns null for invalid priority values', () => {
    expect(normalizePriority('urgent')).toBeNull();
    expect(normalizePriority('p5')).toBeNull();
    expect(normalizePriority('high')).toBeNull();
    expect(normalizePriority('')).toBeNull();
  });

  it('returns null for undefined', () => {
    expect(normalizePriority(undefined)).toBeNull();
  });
});

describe('Body Ingest - API Routes', () => {
  let db: DatabaseManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stateManager: StateManager;
  let projectId: string;
  let savedApiKeys: Record<string, string | undefined> = {};

  beforeEach(async () => {
    savedApiKeys = {};
    for (const key of Object.keys(process.env)) {
      if (key.endsWith('_API_KEY')) {
        savedApiKeys[key] = process.env[key];
        delete process.env[key];
      }
    }

    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);

    const projectRepo = stateManager.getProjectRepo();
    const issueRepo = stateManager.getIssueRepo();
    const configRepo = stateManager.getConfigRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();

    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);

    const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
    projectId = project.id;
    projectService.setCurrent(project);
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

  function setupServer(): http.Server {
    const app = new Hono();
    const eventBus = new EventBus();
    const agentRunner = new AgentRunnerService(eventBus);
    app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, undefined, agentRunner));
    const srv = createTestServer(app);
    srv.listen(0);
    return srv;
  }

  describe('POST /api/issues with uppercase priority', () => {
    it('accepts P0 priority and normalizes to p0', async () => {
      const server = setupServer();
      try {
        const response = await request(server)
          .post('/api/issues')
          .send({ title: 'Urgent Issue', priority: 'P0' });

        expect(response.status).toBe(201);
        expect(response.body.data.priority).toBe('p0');
      } finally {
        server.close();
      }
    });

    it('accepts P2 priority and normalizes to p2', async () => {
      const server = setupServer();
      try {
        const response = await request(server)
          .post('/api/issues')
          .send({ title: 'Medium Issue', priority: 'P2' });

        expect(response.status).toBe(201);
        expect(response.body.data.priority).toBe('p2');
      } finally {
        server.close();
      }
    });

    it('accepts P4 priority and normalizes to p4', async () => {
      const server = setupServer();
      try {
        const response = await request(server)
          .post('/api/issues')
          .send({ title: 'Low Issue', priority: 'P4' });

        expect(response.status).toBe(201);
        expect(response.body.data.priority).toBe('p4');
      } finally {
        server.close();
      }
    });

    it('returns 400 for invalid priority on create', async () => {
      const server = setupServer();
      try {
        const response = await request(server)
          .post('/api/issues')
          .send({ title: 'Bad Priority', priority: 'urgent' });

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('Invalid priority');
      } finally {
        server.close();
      }
    });
  });

  describe('PATCH /api/issues/:number with uppercase priority', () => {
    it('accepts P1 priority on update and normalizes to p1', async () => {
      const server = setupServer();
      try {
        await request(server)
          .post('/api/issues')
          .send({ title: 'Test', priority: 'p2' });

        const response = await request(server)
          .patch('/api/issues/1')
          .send({ priority: 'P1' });

        expect(response.status).toBe(200);
        expect(response.body.data.priority).toBe('p1');
      } finally {
        server.close();
      }
    });

    it('accepts P3 priority on update and normalizes to p3', async () => {
      const server = setupServer();
      try {
        await request(server)
          .post('/api/issues')
          .send({ title: 'Test', priority: 'p2' });

        const response = await request(server)
          .patch('/api/issues/1')
          .send({ priority: 'P3' });

        expect(response.status).toBe(200);
        expect(response.body.data.priority).toBe('p3');
      } finally {
        server.close();
      }
    });

    it('returns 400 for invalid priority on update', async () => {
      const server = setupServer();
      try {
        await request(server)
          .post('/api/issues')
          .send({ title: 'Test' });

        const response = await request(server)
          .patch('/api/issues/1')
          .send({ priority: 'invalid' });

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('Invalid priority');
      } finally {
        server.close();
      }
    });
  });

  describe('GET /api/issues with uppercase priority filter', () => {
    it('filters by P1 the same as p1', async () => {
      const server = setupServer();
      try {
        await request(server)
          .post('/api/issues')
          .send({ title: 'P1 Issue', priority: 'p1' });
        await request(server)
          .post('/api/issues')
          .send({ title: 'P2 Issue', priority: 'p2' });
        await request(server)
          .post('/api/issues')
          .send({ title: 'P1 Another', priority: 'p1' });

        const response = await request(server)
          .get('/api/issues?priority=P1');

        expect(response.status).toBe(200);
        expect(response.body.data).toHaveLength(2);
        expect(response.body.data.every((i: any) => i.priority === 'p1')).toBe(true);
      } finally {
        server.close();
      }
    });

    it('filters by P3 the same as p3', async () => {
      const server = setupServer();
      try {
        await request(server)
          .post('/api/issues')
          .send({ title: 'P3 Issue', priority: 'p3' });
        await request(server)
          .post('/api/issues')
          .send({ title: 'P2 Issue', priority: 'p2' });

        const response = await request(server)
          .get('/api/issues?priority=P3');

        expect(response.status).toBe(200);
        expect(response.body.data).toHaveLength(1);
        expect(response.body.data[0].priority).toBe('p3');
      } finally {
        server.close();
      }
    });

    it('returns 400 for invalid priority filter', async () => {
      const server = setupServer();
      try {
        const response = await request(server)
          .get('/api/issues?priority=bad');

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('Invalid priority');
      } finally {
        server.close();
      }
    });
  });
});

describe('Issue Create - API Contract', () => {
  let db: DatabaseManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stateManager: StateManager;
  let projectId: string;
  let savedApiKeys: Record<string, string | undefined> = {};

  beforeEach(async () => {
    savedApiKeys = {};
    for (const key of Object.keys(process.env)) {
      if (key.endsWith('_API_KEY')) {
        savedApiKeys[key] = process.env[key];
        delete process.env[key];
      }
    }

    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);

    const projectRepo = stateManager.getProjectRepo();
    const issueRepo = stateManager.getIssueRepo();
    const configRepo = stateManager.getConfigRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();

    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);

    const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
    projectId = project.id;
    projectService.setCurrent(project);
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

  function setupServer(): http.Server {
    const app = new Hono();
    const eventBus = new EventBus();
    const agentRunner = new AgentRunnerService(eventBus);
    app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, undefined, agentRunner));
    const srv = createTestServer(app);
    srv.listen(0);
    return srv;
  }

  describe('POST /api/issues with body', () => {
    it('accepts body content via API', async () => {
      const server = setupServer();
      try {
        const response = await request(server)
          .post('/api/issues')
          .send({ title: 'Body Test', body: '# Markdown body\n\nWith content' });

        expect(response.status).toBe(201);
        expect(response.body.data.body).toBe('# Markdown body\n\nWith content');
      } finally {
        server.close();
      }
    });
  });
});

describe('normalizePriority exported from types', () => {
  it('is exported and functional', () => {
    expect(typeof normalizePriority).toBe('function');
    expect(normalizePriority('P2')).toBe('p2');
  });

  it('ingestBody is exported and functional', async () => {
    expect(typeof ingestBody).toBe('function');
    const result = await ingestBody('test', undefined);
    expect(result.body).toBe('test');
  });

  it('BodyIngestResult interface is present via function signature', () => {
    const result: BodyIngestResult = { body: 'test', error: null };
    expect(result.body).toBe('test');
    expect(result.error).toBeNull();
  });
});