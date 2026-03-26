import { Router, Request, Response } from 'express';
import { StateManager } from '../server/state-manager';
import { ApiResponse } from '../types';

export function createLabelRoutes(stateManager: StateManager): Router {
  const router = Router();

  router.get('/', (_req: Request, res: Response): void => {
    try {
      const projectId = stateManager.getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: crawlph project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      const labels = stateManager.getLabels(projectId);

      const response: ApiResponse<string[]> = {
        success: true,
        data: labels
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
