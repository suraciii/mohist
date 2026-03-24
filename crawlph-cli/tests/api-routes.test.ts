import { describe, it, expect, beforeEach, vi } from 'vitest';
import express from 'express';
import request from 'supertest';

function createMockProjectManager() {
  return {
    create: vi.fn(),
    list: vi.fn(),
    get: vi.fn(),
    delete: vi.fn(),
    use: vi.fn(),
    getCurrent: vi.fn(),
    setCurrentById: vi.fn()
  };
}

function createMockTaskQueue() {
  return {
    enqueue: vi.fn().mockReturnValue('task-uuid-123'),
    dequeue: vi.fn(),
    complete: vi.fn(),
    getQueueLength: vi.fn().mockReturnValue(0),
    getRunningCount: vi.fn().mockReturnValue(0),
    getPendingTasks: vi.fn().mockReturnValue([]),
    getRunningTasks: vi.fn().mockReturnValue([]),
    getAllTasks: vi.fn().mockReturnValue([]),
    canStartNew: vi.fn().mockReturnValue(true),
    clear: vi.fn()
  };
}

function createMockStateManager() {
  return {
    saveProjects: vi.fn(),
    loadProjects: vi.fn().mockReturnValue([]),
    saveState: vi.fn(),
    loadState: vi.fn().mockReturnValue(null)
  };
}

describe('API Routes', () => {
  describe('Issue Routes', () => {
    let app: express.Express;
    let mockProjectManager: ReturnType<typeof createMockProjectManager>;
    let mockTaskQueue: ReturnType<typeof createMockTaskQueue>;

    beforeEach(async () => {
      vi.clearAllMocks();
      mockProjectManager = createMockProjectManager();
      mockTaskQueue = createMockTaskQueue();
      
      app = express();
      app.use(express.json());
      
      const { createIssueRoutes } = await import('../src/api/issues');
      app.use('/api/issues', createIssueRoutes(mockProjectManager as any, mockTaskQueue as any));
    });

    describe('GET /api/issues', () => {
      it('should return error when no current project', async () => {
        mockProjectManager.getCurrent.mockReturnValue(undefined);

        const response = await request(app).get('/api/issues');

        expect(response.status).toBe(400);
        expect(response.body.success).toBe(false);
        expect(response.body.error).toBe('No current project');
      });

      it('should return empty issues array when project is set', async () => {
        mockProjectManager.getCurrent.mockReturnValue({
          id: 'project-1',
          name: 'Test Project',
          repo: 'owner/repo',
          path: '/path/to/project',
          createdAt: '2024-01-01T00:00:00Z',
          updatedAt: '2024-01-01T00:00:00Z'
        });

        const response = await request(app).get('/api/issues');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data).toEqual([]);
      });
    });

    describe('GET /api/issues/:number', () => {
      it('should return error when no current project', async () => {
        mockProjectManager.getCurrent.mockReturnValue(undefined);

        const response = await request(app).get('/api/issues/1');

        expect(response.status).toBe(400);
        expect(response.body.error).toBe('No current project');
      });

      it('should return 404 for non-existent issue', async () => {
        mockProjectManager.getCurrent.mockReturnValue({
          id: 'project-1',
          name: 'Test Project'
        });

        const response = await request(app).get('/api/issues/999');

        expect(response.status).toBe(404);
        expect(response.body.error).toContain('not found');
      });
    });

    describe('POST /api/issues/:number/start', () => {
      it('should return error when no current project', async () => {
        mockProjectManager.getCurrent.mockReturnValue(undefined);

        const response = await request(app).post('/api/issues/1/start');

        expect(response.status).toBe(400);
        expect(response.body.error).toBe('No current project');
      });

      it('should enqueue task when project is set', async () => {
        mockProjectManager.getCurrent.mockReturnValue({
          id: 'project-1',
          name: 'Test Project'
        });

        const response = await request(app).post('/api/issues/1/start');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.taskId).toBe('task-uuid-123');
        expect(mockTaskQueue.enqueue).toHaveBeenCalledWith(1, 'project-1', 'draft');
      });
    });

    describe('POST /api/issues/:number/pause', () => {
      it('should return success message', async () => {
        const response = await request(app).post('/api/issues/1/pause');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.message).toContain('paused');
      });
    });

    describe('POST /api/issues/:number/resume', () => {
      it('should return success message', async () => {
        const response = await request(app).post('/api/issues/1/resume');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.message).toContain('resumed');
      });
    });
  });

  describe('PR Routes', () => {
    let app: express.Express;
    let mockProjectManager: ReturnType<typeof createMockProjectManager>;

    beforeEach(async () => {
      vi.clearAllMocks();
      mockProjectManager = createMockProjectManager();
      
      app = express();
      app.use(express.json());
      
      const { createPullRequestRoutes } = await import('../src/api/prs');
      app.use('/api/prs', createPullRequestRoutes(mockProjectManager as any));
    });

    describe('GET /api/prs', () => {
      it('should return error when no current project', async () => {
        mockProjectManager.getCurrent.mockReturnValue(undefined);

        const response = await request(app).get('/api/prs');

        expect(response.status).toBe(400);
        expect(response.body.error).toBe('No current project');
      });

      it('should return empty PRs array when project is set', async () => {
        mockProjectManager.getCurrent.mockReturnValue({
          id: 'project-1',
          name: 'Test Project'
        });

        const response = await request(app).get('/api/prs');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data).toEqual([]);
      });
    });

    describe('GET /api/prs/:number', () => {
      it('should return error when no current project', async () => {
        mockProjectManager.getCurrent.mockReturnValue(undefined);

        const response = await request(app).get('/api/prs/1');

        expect(response.status).toBe(400);
        expect(response.body.error).toBe('No current project');
      });

      it('should return 404 for non-existent PR', async () => {
        mockProjectManager.getCurrent.mockReturnValue({
          id: 'project-1',
          name: 'Test Project'
        });

        const response = await request(app).get('/api/prs/999');

        expect(response.status).toBe(404);
        expect(response.body.error).toContain('not found');
      });
    });

    describe('POST /api/prs/:number/approve', () => {
      it('should return success message', async () => {
        const response = await request(app).post('/api/prs/1/approve');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.message).toContain('approved');
      });
    });
  });

  describe('Project Routes', () => {
    let app: express.Express;
    let mockProjectManager: ReturnType<typeof createMockProjectManager>;
    let mockStateManager: ReturnType<typeof createMockStateManager>;

    beforeEach(async () => {
      vi.clearAllMocks();
      mockProjectManager = createMockProjectManager();
      mockStateManager = createMockStateManager();
      
      app = express();
      app.use(express.json());
      
      const { createProjectRoutes } = await import('../src/api/projects');
      app.use('/api/projects', createProjectRoutes(mockProjectManager as any, mockStateManager as any));
    });

    describe('POST /api/projects', () => {
      it('should create project', async () => {
        mockProjectManager.get.mockReturnValue(undefined);
        mockProjectManager.create.mockReturnValue({
          id: 'project-1',
          name: 'New Project',
          repo: 'owner/repo'
        });
        mockProjectManager.list.mockReturnValue([
          { id: 'project-1', name: 'New Project', repo: 'owner/repo' }
        ]);

        const response = await request(app)
          .post('/api/projects')
          .send({ name: 'New Project', repo: 'owner/repo' });

        expect(response.status).toBe(201);
        expect(response.body.success).toBe(true);
        expect(mockProjectManager.create).toHaveBeenCalledWith('New Project', 'owner/repo');
        expect(mockStateManager.saveProjects).toHaveBeenCalled();
      });

      it('should require repo parameter', async () => {
        const response = await request(app)
          .post('/api/projects')
          .send({ name: 'New Project' });

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('repo');
      });
      
      it('should reject duplicate project name', async () => {
        mockProjectManager.get.mockReturnValue({
          id: 'existing-project',
          name: 'New Project',
          repo: 'owner/other'
        });

        const response = await request(app)
          .post('/api/projects')
          .send({ name: 'New Project', repo: 'owner/repo' });

        expect(response.status).toBe(409);
        expect(response.body.error).toContain('already exists');
      });
    });

    describe('GET /api/projects', () => {
      it('should list projects', async () => {
        mockProjectManager.list.mockReturnValue([
          { id: 'project-1', name: 'Project 1' },
          { id: 'project-2', name: 'Project 2' }
        ]);

        const response = await request(app).get('/api/projects');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data).toHaveLength(2);
      });
    });
  });

  describe('Status Routes', () => {
    let app: express.Express;
    let mockProjectManager: ReturnType<typeof createMockProjectManager>;

    beforeEach(async () => {
      vi.clearAllMocks();
      mockProjectManager = createMockProjectManager();
      
      app = express();
      app.use(express.json());
      
      const { createStatusRoutes } = await import('../src/api/status');
      app.use('/api/status', createStatusRoutes(mockProjectManager as any));
    });

    describe('GET /api/status', () => {
      it('should return error when no current project', async () => {
        mockProjectManager.getCurrent.mockReturnValue(undefined);

        const response = await request(app).get('/api/status');

        expect(response.status).toBe(400);
        expect(response.body.error).toContain('No current project');
      });

      it('should return current project status', async () => {
        mockProjectManager.getCurrent.mockReturnValue({
          id: 'project-1',
          name: 'Test Project',
          repo: 'owner/repo'
        });

        const response = await request(app).get('/api/status');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data.name).toBe('Test Project');
      });

      it('should return all projects status with ?all=true', async () => {
        mockProjectManager.list.mockReturnValue([
          { id: 'project-1', name: 'Project 1', repo: 'owner/repo1' },
          { id: 'project-2', name: 'Project 2', repo: 'owner/repo2' }
        ]);

        const response = await request(app).get('/api/status?all=true');

        expect(response.status).toBe(200);
        expect(response.body.success).toBe(true);
        expect(response.body.data).toHaveLength(2);
      });
    });
  });
});
