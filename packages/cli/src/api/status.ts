import { Hono } from 'hono';
import { StateManager } from '../server/state-manager';
import { ApiResponse } from '../types';

export function createStatusRoutes(
  stateManager: StateManager
): Hono {
  const app = new Hono();

  app.get('/status', async (c) => {
    try {
      const all = c.req.query('all') === 'true';
      
      if (all) {
        const projects = stateManager.loadProjects();
        const status = projects.map(p => {
          const issues = stateManager.loadIssues(p.id);
          const activeIssues = issues.filter(i => i.status === 'active').length;
          
          return {
            name: p.name,
            path: p.path,
            issues: issues.length,
            activeIssues,
            isCurrent: stateManager.getCurrentProjectId() === p.id
          };
        });
        
        const response: ApiResponse = {
          success: true,
          data: status
        };
        return c.json(response);
      }

      const currentId = stateManager.getCurrentProjectId();
      if (!currentId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const current = stateManager.getProjectById(currentId);
      if (!current) {
        const response: ApiResponse = {
          success: false,
          error: 'Current project not found'
        };
        return c.json(response, 404);
      }

      const issues = stateManager.loadIssues(currentId);
      const activeIssues = issues.filter(i => i.status === 'active');

      const status = {
        name: current.name,
        path: current.path,
        issues: issues.length,
        activeIssues: activeIssues.length,
        issuesByStage: {
          draft: issues.filter(i => i.stage === 'draft').length,
          plan: issues.filter(i => i.stage === 'plan').length,
          build: issues.filter(i => i.stage === 'build').length,
          check: issues.filter(i => i.stage === 'check').length,
          done: issues.filter(i => i.stage === 'done').length,
        }
      };

      const response: ApiResponse = {
        success: true,
        data: status
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.get('/health', async (c) => {
    return c.json({ status: 'ok', timestamp: new Date().toISOString() });
  });

  return app;
}
