import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import http from 'node:http';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase, getSchemaVersion } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { ConfigService } from '../src/services/config-service';
import { EventBus, AgentRunnerService } from '../src/services';
import { StateManager } from '../src/server/state-manager';
import { createIssueRoutes } from '../src/api/issues';
import { Stage, IssueStatus, Priority, VALID_PRIORITIES } from '../src/types';
import { Command } from 'commander';
import { setupIssueCommands } from '../src/cli/commands/issue';

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

describe('Priority - IssueRepo', () => {
  let db: DatabaseManager;
  let repo: IssueRepo;
  let projectId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    projectId = project.id;

    repo = new IssueRepo(db);
  });

  afterEach(() => {
    db.close();
  });

  describe('create with priority', () => {
    it('should create issue with specified priority', () => {
      const issue = repo.create({
        number: 1,
        projectId,
        title: 'Urgent',
        priority: 'p0',
      });

      expect(issue.priority).toBe('p0');
    });

    it('should default to p2 when no priority specified', () => {
      const issue = repo.create({
        number: 1,
        projectId,
        title: 'Normal',
      });

      expect(issue.priority).toBe('p2');
    });
  });

  describe('findAll sort order', () => {
    it('should sort by priority ASC, number ASC', () => {
      repo.create({ number: 3, projectId, title: 'P1 issue', priority: 'p1' });
      repo.create({ number: 1, projectId, title: 'P2 issue A', priority: 'p2' });
      repo.create({ number: 5, projectId, title: 'P0 issue', priority: 'p0' });
      repo.create({ number: 2, projectId, title: 'P2 issue B', priority: 'p2' });
      repo.create({ number: 4, projectId, title: 'P3 issue', priority: 'p3' });

      const issues = repo.findAll({ projectId });

      expect(issues.map(i => `${i.priority}#${i.number}`)).toEqual([
        'p0#5',
        'p1#3',
        'p2#1',
        'p2#2',
        'p3#4',
      ]);
    });
  });

  describe('findAll with priority filter', () => {
    it('should return only issues matching priority filter', () => {
      repo.create({ number: 1, projectId, title: 'A', priority: 'p0' });
      repo.create({ number: 2, projectId, title: 'B', priority: 'p2' });
      repo.create({ number: 3, projectId, title: 'C', priority: 'p0' });
      repo.create({ number: 4, projectId, title: 'D', priority: 'p3' });

      const p0Issues = repo.findAll({ projectId, priority: 'p0' });

      expect(p0Issues).toHaveLength(2);
      expect(p0Issues.every(i => i.priority === 'p0')).toBe(true);
    });

    it('should return empty array for unmatched priority', () => {
      repo.create({ number: 1, projectId, title: 'A', priority: 'p2' });

      const p0Issues = repo.findAll({ projectId, priority: 'p0' });

      expect(p0Issues).toHaveLength(0);
    });
  });

  describe('update priority', () => {
    it('should update issue priority', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test', priority: 'p2' });
      const updated = repo.update(issue.id, { priority: 'p0' });

      expect(updated?.priority).toBe('p0');
    });

    it('should persist priority update', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test', priority: 'p2' });
      repo.update(issue.id, { priority: 'p1' });

      const found = repo.findById(issue.id);
      expect(found?.priority).toBe('p1');
    });
  });
});

describe('Priority - API Routes', () => {
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
    return createTestServer(app);
  }

  describe('POST /api/issues with priority', () => {
    it('should create issue with priority p0', async () => {
      const server = setupServer();

      const response = await request(server)
        .post('/api/issues')
        .send({ title: 'Urgent', priority: 'p0' });

      expect(response.body.data.priority).toBe('p0');
    });

    it('should default to p2 when priority not specified', async () => {
      const server = setupServer();

      const response = await request(server)
        .post('/api/issues')
        .send({ title: 'Normal' });

      expect(response.status).toBe(201);
      expect(response.body.data.priority).toBe('p2');
    });

    it('should return 400 for invalid priority', async () => {
      const server = setupServer();

      const response = await request(server)
        .post('/api/issues')
        .send({ title: 'Bad Priority', priority: 'p5' });

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('Invalid priority');
    });

    it('should return 400 for non-priority string', async () => {
      const server = setupServer();

      const response = await request(server)
        .post('/api/issues')
        .send({ title: 'Bad Priority', priority: 'high' });

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('Invalid priority');
    });
  });

  describe('PATCH /api/issues/:number with priority', () => {
    it('should update priority', async () => {
      const server = setupServer();

      await request(server)
        .post('/api/issues')
        .send({ title: 'Test', priority: 'p2' });

      const response = await request(server)
        .patch('/api/issues/1')
        .send({ priority: 'p0' });

      expect(response.status).toBe(200);
      expect(response.body.data.priority).toBe('p0');
    });

    it('should return 400 for invalid priority on update', async () => {
      const server = setupServer();

      await request(server)
        .post('/api/issues')
        .send({ title: 'Test' });

      const response = await request(server)
        .patch('/api/issues/1')
        .send({ priority: 'invalid' });

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('Invalid priority');
    });
  });

  describe('GET /api/issues with priority filter', () => {
    it('should filter by priority', async () => {
      const server = setupServer();

      await request(server)
        .post('/api/issues')
        .send({ title: 'Urgent', priority: 'p0' });

      await request(server)
        .post('/api/issues')
        .send({ title: 'Normal', priority: 'p2' });

      await request(server)
        .post('/api/issues')
        .send({ title: 'Another Urgent', priority: 'p0' });

      const response = await request(server)
        .get('/api/issues?priority=p0');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(2);
      expect(response.body.data.every((i: any) => i.priority === 'p0')).toBe(true);
    });

    it('should return 400 for invalid priority filter', async () => {
      const server = setupServer();

      const response = await request(server)
        .get('/api/issues?priority=invalid');

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('Invalid priority');
    });

    it('should return issues sorted by priority ASC by default', async () => {
      const server = setupServer();

      await request(server)
        .post('/api/issues')
        .send({ title: 'P2 first', priority: 'p2' });

      await request(server)
        .post('/api/issues')
        .send({ title: 'P0', priority: 'p0' });

      await request(server)
        .post('/api/issues')
        .send({ title: 'P2 second', priority: 'p2' });

      const response = await request(server).get('/api/issues');

      expect(response.status).toBe(200);
      const priorities = response.body.data.map((i: any) => i.priority);
      expect(priorities).toEqual(['p0', 'p2', 'p2']);
    });
  });
});

describe('Priority - CLI Commands', () => {
  describe('issue create --priority', () => {
    it('should have --priority option on create command', () => {
      const program = new Command();
      setupIssueCommands(program);

      const issueCmd = program.commands.find(cmd => cmd.name() === 'issue');
      const createCmd = issueCmd?.commands.find(cmd => cmd.name() === 'create');

      expect(createCmd?.options.some(opt => opt.long === '--priority')).toBe(true);
    });
  });

  describe('issue update --priority', () => {
    it('should have --priority option on update command', () => {
      const program = new Command();
      setupIssueCommands(program);

      const issueCmd = program.commands.find(cmd => cmd.name() === 'issue');
      const updateCmd = issueCmd?.commands.find(cmd => cmd.name() === 'update');

      expect(updateCmd?.options.some(opt => opt.long === '--priority')).toBe(true);
    });
  });

  describe('issue list --priority', () => {
    it('should have --priority option on list command', () => {
      const program = new Command();
      setupIssueCommands(program);

      const issueCmd = program.commands.find(cmd => cmd.name() === 'issue');
      const listCmd = issueCmd?.commands.find(cmd => cmd.name() === 'list');

      expect(listCmd?.options.some(opt => opt.long === '--priority')).toBe(true);
    });
  });
});

describe('Priority - Types', () => {
  it('should have VALID_PRIORITIES containing p0-p4', () => {
    expect(VALID_PRIORITIES).toEqual(['p0', 'p1', 'p2', 'p3', 'p4']);
  });

  it('should accept valid Priority values', () => {
    const valid: Priority[] = ['p0', 'p1', 'p2', 'p3', 'p4'];
    for (const p of valid) {
      expect(VALID_PRIORITIES.includes(p)).toBe(true);
    }
  });
});
