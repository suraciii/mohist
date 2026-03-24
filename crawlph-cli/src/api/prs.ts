import { Router, Request, Response } from 'express';
import { ProjectManager } from '../project/manager';
import { ApiResponse, PullRequest } from '../types';

export function createPullRequestRoutes(projectManager: ProjectManager): Router {
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

      const prs: PullRequest[] = [];
      const response: ApiResponse<PullRequest[]> = {
        success: true,
        data: prs
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
        error: `Pull request #${number} not found`
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

  router.post('/:number/approve', (req: Request, res: Response): void => {
    try {
      const number = parseInt(req.params.number);
      
      const response: ApiResponse = {
        success: true,
        data: { message: `Pull request #${number} approved` }
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
