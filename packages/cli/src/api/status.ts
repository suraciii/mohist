import { Router, Request, Response } from 'express';
import { StateManager } from '../server/state-manager';
import { TaskQueue } from '../server/task-queue';
import { ApiResponse } from '../types';

export function createStatusRoutes(
  stateManager: StateManager,
  taskQueue: TaskQueue
): Router {
  const router = Router();

  router.get('/status', (req: Request, res: Response): void => {
    try {
      const all = req.query.all === 'true';
      
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
        res.json(response);
        return;
      }

      const currentId = stateManager.getCurrentProjectId();
      if (!currentId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: crawlph project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      const current = stateManager.getProjectById(currentId);
      if (!current) {
        const response: ApiResponse = {
          success: false,
          error: 'Current project not found'
        };
        res.status(404).json(response);
        return;
      }

      const issues = stateManager.loadIssues(currentId);
      const activeIssues = issues.filter(i => i.status === 'active');
      const runningTasks = taskQueue.getRunningCount();
      const queuedTasks = taskQueue.getQueueLength();

      const status = {
        name: current.name,
        path: current.path,
        issues: issues.length,
        activeIssues: activeIssues.length,
        runningTasks,
        queuedTasks,
        issuesByStage: {
          draft: issues.filter(i => i.stage === 'draft').length,
          designing: issues.filter(i => i.stage === 'designing').length,
          waitingDesignReview: issues.filter(i => i.stage === 'waiting-design-review').length,
          implementing: issues.filter(i => i.stage === 'implementing').length,
          waitingReview: issues.filter(i => i.stage === 'waiting-review').length,
          done: issues.filter(i => i.stage === 'done').length,
        }
      };

      const response: ApiResponse = {
        success: true,
        data: status
      };
      res.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.get('/health', (_req: Request, res: Response): void => {
    res.json({ status: 'ok', timestamp: new Date().toISOString() });
  });

  return router;
}
