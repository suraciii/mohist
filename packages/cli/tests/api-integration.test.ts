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
    
    it('should export StateManager', async () => {
      const { StateManager } = await import('../src/server/state-manager');
      expect(typeof StateManager).toBe('function');
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
