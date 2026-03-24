import { describe, it, expect } from 'vitest';

describe('API Integration Tests', () => {
  describe('API Structure', () => {
    it('should export createProjectRoutes', async () => {
      const { createProjectRoutes } = await import('../src/api/projects');
      expect(typeof createProjectRoutes).toBe('function');
    });
    
    it('should export createIssueRoutes', async () => {
      const { createIssueRoutes } = await import('../src/api/issues');
      expect(typeof createIssueRoutes).toBe('function');
    });
    
    it('should export createPullRequestRoutes', async () => {
      const { createPullRequestRoutes } = await import('../src/api/prs');
      expect(typeof createPullRequestRoutes).toBe('function');
    });
    
    it('should export createStatusRoutes', async () => {
      const { createStatusRoutes } = await import('../src/api/status');
      expect(typeof createStatusRoutes).toBe('function');
    });
    
    it('should export createConfigRoutes', async () => {
      const { createConfigRoutes } = await import('../src/api/config');
      expect(typeof createConfigRoutes).toBe('function');
    });
    
    it('should export errorHandler', async () => {
      const { errorHandler } = await import('../src/api/error-handler');
      expect(typeof errorHandler).toBe('function');
    });
    
    it('should export notFoundHandler', async () => {
      const { notFoundHandler } = await import('../src/api/error-handler');
      expect(typeof notFoundHandler).toBe('function');
    });
  });
  
  describe('Server Structure', () => {
    it('should export HttpServer', async () => {
      const { HttpServer } = await import('../src/server/http-server');
      expect(typeof HttpServer).toBe('function');
    });
    
    it('should export TaskQueue', async () => {
      const { TaskQueue } = await import('../src/server/task-queue');
      expect(typeof TaskQueue).toBe('function');
    });
    
    it('should export StateManager', async () => {
      const { StateManager } = await import('../src/server/state-manager');
      expect(typeof StateManager).toBe('function');
    });
  });
  
  describe('Workflow Structure', () => {
    it('should export stage transitions', async () => {
      const { STAGE_TRANSITIONS, getNextStage, canStartAgent } = await import('../src/workflow/issue-workflow');
      expect(STAGE_TRANSITIONS).toBeDefined();
      expect(typeof getNextStage).toBe('function');
      expect(typeof canStartAgent).toBe('function');
    });
    
    it('should export stage handlers', async () => {
      const { getStageHandler, DesigningHandler, ImplementingHandler } = await import('../src/workflow/stage-handlers');
      expect(typeof getStageHandler).toBe('function');
      expect(typeof DesigningHandler).toBe('function');
      expect(typeof ImplementingHandler).toBe('function');
    });
  });
});

describe('Server Running Tests', () => {
  it.skip('should pass health check when server is running', async () => {
    const http = await import('http');
    
    await new Promise<void>((resolve, reject) => {
      const req = http.get('http://localhost:3456/api/health', (res) => {
        expect(res.statusCode).toBe(200);
        resolve();
      });
      req.on('error', () => {
        reject(new Error('Server not running'));
      });
      req.setTimeout(1000, () => {
        req.destroy();
        reject(new Error('Timeout'));
      });
    });
  });
});
