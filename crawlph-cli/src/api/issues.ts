import { Router, Request, Response } from 'express';
import { ProjectManager } from '../project/manager';
import { TaskQueue } from '../server/task-queue';
import { ApiResponse, Issue } from '../types';

export function createIssueRoutes(
  projectManager: ProjectManager,
  taskQueue: TaskQueue
): Router {
  const router = Router();

  router.get('/', (_req: Request, res: Response): void => {
    try {
      const current = projectManager.getCurrent();
      if (!current) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project'
        };
        res.status(400).json(response);
        return;
      }

      const issues: Issue[] = [];
      const response: ApiResponse<Issue[]> = {
        success: true,
        data: issues
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

  router.get('/:number', (req: Request, res: Response): void => {
    try {
      const number = parseInt(req.params.number);
      const current = projectManager.getCurrent();
      
      if (!current) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project'
        };
        res.status(400).json(response);
        return;
      }

      const response: ApiResponse = {
        success: false,
        error: `Issue #${number} not found`
      };
      res.status(404).json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.post('/:number/start', (req: Request, res: Response): void => {
    try {
      const number = parseInt(req.params.number);
      const current = projectManager.getCurrent();
      
      if (!current) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project'
        };
        res.status(400).json(response);
        return;
      }

      const taskId = taskQueue.enqueue(number, current.id, 'draft');
      
      const response: ApiResponse = {
        success: true,
        data: { taskId }
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

  router.post('/:number/pause', (req: Request, res: Response): void => {
    try {
      const number = parseInt(req.params.number);
      
      const response: ApiResponse = {
        success: true,
        data: { message: `Issue #${number} paused` }
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

  router.post('/:number/resume', (req: Request, res: Response): void => {
    try {
      const number = parseInt(req.params.number);
      
      const response: ApiResponse = {
        success: true,
        data: { message: `Issue #${number} resumed` }
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

  return router;
}
