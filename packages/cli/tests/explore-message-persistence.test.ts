import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import http from 'node:http';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ExploreSessionRepo } from '../src/db/explore-session-repo';
import { ExploreMessageRepo } from '../src/db/explore-message-repo';
import { ExploreService } from '../src/services/explore-service';
import { IssueService } from '../src/services/issue-service';
import { ProjectService } from '../src/services/project-service';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { EventBus } from '../src/services/event-bus';
import { ExploreStatus } from '../src/types';

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

const mockRunExploreAgent = vi.fn();

vi.mock('../src/agents/explore-agent', () => ({
  runExploreAgent: (...args: unknown[]) => mockRunExploreAgent(...args),
}));

describe('Explore Message Persistence', () => {
  let db: DatabaseManager;
  let exploreService: ExploreService;
  let issueService: IssueService;
  let projectService: ProjectService;
  let exploreSessionRepo: ExploreSessionRepo;
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
    initializeDatabase(db);

    exploreSessionRepo = new ExploreSessionRepo(db);
    const exploreMessageRepo = new ExploreMessageRepo(db);
    exploreService = new ExploreService(exploreSessionRepo, exploreMessageRepo);

    const projectRepo = new ProjectRepo(db);
    const configRepo = new ConfigRepo(db);
    const issueRepo = new IssueRepo(db);
    const commentRepo = new CommentRepo(db);
    const labelRepo = new LabelRepo(db);

    issueService = new IssueService(issueRepo, commentRepo);
    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);

    mockRunExploreAgent.mockReset();
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

  async function setupApp() {
    const { createExploreRoutes } = await import('../src/api/explore');
    const eventBus = new EventBus();
    const app = new Hono();
    app.route('/api/explore', createExploreRoutes(
      exploreService,
      issueService,
      projectService,
      exploreSessionRepo,
      eventBus,
    ));
    return createTestServer(app);
  }

  describe('User message persisted before LLM call', () => {
    it('should persist user message even when runExploreAgent throws', async () => {
      const project = await projectService.create({ name: 'Test', path: '/test' });
      projectService.setCurrent(project);
      const session = exploreService.createSession({
        projectId: project.id,
        title: 'Test Session',
      });

      mockRunExploreAgent.mockRejectedValue(new Error('LLM unavailable'));

      const server = await setupApp();

      const response = await request(server)
        .post(`/api/explore/${session.id}/messages`)
        .send({ content: 'Hello, explore!' });

      expect(response.status).toBe(500);

      const messages = exploreService.getMessages(session.id);
      expect(messages).toHaveLength(1);
      expect(messages[0].role).toBe('user');
      expect(messages[0].content).toBe('Hello, explore!');
    });
  });

  describe('Partial assistant message saved on stream error', () => {
    it('should save partial assistant content when stream throws mid-way', async () => {
      const project = await projectService.create({ name: 'Test', path: '/test' });
      projectService.setCurrent(project);
      const session = exploreService.createSession({
        projectId: project.id,
        title: 'Test Session',
      });

      async function* mockStream() {
        yield { type: 'text-delta', text: 'Hello' };
        yield { type: 'text-delta', text: ' world' };
        throw new Error('Stream interrupted');
      }

      mockRunExploreAgent.mockResolvedValue({
        fullStream: mockStream(),
        text: Promise.resolve('Hello world'),
      });

      const server = await setupApp();

      const response = await request(server)
        .post(`/api/explore/${session.id}/messages`)
        .send({ content: 'Tell me something' });

      const messages = exploreService.getMessages(session.id);
      expect(messages).toHaveLength(2);
      expect(messages[0].role).toBe('user');
      expect(messages[0].content).toBe('Tell me something');
      expect(messages[1].role).toBe('assistant');
      expect(messages[1].content).toBe('Hello world');
    });
  });

  describe('No empty assistant message on immediate stream failure', () => {
    it('should not save empty assistant message when stream fails before any chunks', async () => {
      const project = await projectService.create({ name: 'Test', path: '/test' });
      projectService.setCurrent(project);
      const session = exploreService.createSession({
        projectId: project.id,
        title: 'Test Session',
      });

      async function* mockStream() {
        throw new Error('Immediate failure');
      }

      mockRunExploreAgent.mockResolvedValue({
        fullStream: mockStream(),
        text: Promise.resolve(''),
      });

      const server = await setupApp();

      const response = await request(server)
        .post(`/api/explore/${session.id}/messages`)
        .send({ content: 'Break immediately' });

      const messages = exploreService.getMessages(session.id);
      expect(messages).toHaveLength(1);
      expect(messages[0].role).toBe('user');
      expect(messages[0].content).toBe('Break immediately');
    });
  });

  describe('SSE stream completes normally', () => {
    it('should save full assistant message on successful stream', async () => {
      const project = await projectService.create({ name: 'Test', path: '/test' });
      projectService.setCurrent(project);
      const session = exploreService.createSession({
        projectId: project.id,
        title: 'Test Session',
      });

      async function* mockStream() {
        yield { type: 'text-delta', text: 'Complete' };
        yield { type: 'text-delta', text: ' response' };
      }

      mockRunExploreAgent.mockResolvedValue({
        fullStream: mockStream(),
        text: Promise.resolve('Complete response'),
      });

      const server = await setupApp();

      const response = await request(server)
        .post(`/api/explore/${session.id}/messages`)
        .send({ content: 'Say something' });

      const messages = exploreService.getMessages(session.id);
      expect(messages).toHaveLength(2);
      expect(messages[0].role).toBe('user');
      expect(messages[0].content).toBe('Say something');
      expect(messages[1].role).toBe('assistant');
      expect(messages[1].content).toBe('Complete response');
    });
  });

  describe('findByProject status filtering', () => {
    it('should return only sessions matching status filter', async () => {
      const project = await projectService.create({ name: 'Test', path: '/test' });

      const session1 = exploreSessionRepo.create({
        projectId: project.id,
        title: 'Active 1',
      });

      const session2 = exploreSessionRepo.create({
        projectId: project.id,
        title: 'Active 2',
      });

      const session3 = exploreSessionRepo.create({
        projectId: project.id,
        title: 'To crystallize',
      });

      exploreSessionRepo.updateStatus(session3.id, ExploreStatus.Crystallized);

      const activeSessions = exploreSessionRepo.findByProject(project.id, ExploreStatus.Active);
      expect(activeSessions).toHaveLength(2);
      expect(activeSessions.every(s => s.status === ExploreStatus.Active)).toBe(true);

      const crystallizedSessions = exploreSessionRepo.findByProject(project.id, ExploreStatus.Crystallized);
      expect(crystallizedSessions).toHaveLength(1);
      expect(crystallizedSessions[0].id).toBe(session3.id);
      expect(crystallizedSessions[0].status).toBe(ExploreStatus.Crystallized);
    });

    it('should return all sessions when no status filter provided', async () => {
      const project = await projectService.create({ name: 'Test2', path: '/test2' });

      const session1 = exploreSessionRepo.create({
        projectId: project.id,
        title: 'Active 1',
      });

      const session2 = exploreSessionRepo.create({
        projectId: project.id,
        title: 'To crystallize',
      });

      exploreSessionRepo.updateStatus(session2.id, ExploreStatus.Crystallized);

      const allSessions = exploreSessionRepo.findByProject(project.id);
      expect(allSessions).toHaveLength(2);
    });

    it('should return empty for non-existent status value', async () => {
      const project = await projectService.create({ name: 'Test3', path: '/test3' });

      exploreSessionRepo.create({
        projectId: project.id,
        title: 'Active',
      });

      const filtered = exploreSessionRepo.findByProject(project.id, 'nonexistent');
      expect(filtered).toHaveLength(0);
    });
  });

  describe('GET /api/explore with status query parameter', () => {
    it('should filter sessions by status via API', async () => {
      const project = await projectService.create({ name: 'Test4', path: '/test4' });
      projectService.setCurrent(project);

      const session1 = exploreService.createSession({
        projectId: project.id,
        title: 'Active Session',
      });

      const session2 = exploreService.createSession({
        projectId: project.id,
        title: 'Another Active',
      });

      const session3 = exploreService.createSession({
        projectId: project.id,
        title: 'To Crystallize',
      });

      exploreSessionRepo.updateStatus(session3.id, ExploreStatus.Crystallized);

      const server = await setupApp();

      const activeResponse = await request(server)
        .get(`/api/explore?projectId=${project.id}&status=active`);
      expect(activeResponse.status).toBe(200);
      expect(activeResponse.body.data).toHaveLength(2);
      expect(activeResponse.body.data.every((s: any) => s.status === 'active')).toBe(true);

      const allResponse = await request(server)
        .get(`/api/explore?projectId=${project.id}`);
      expect(allResponse.status).toBe(200);
      expect(allResponse.body.data).toHaveLength(3);
    });
  });
});
