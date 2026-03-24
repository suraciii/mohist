import { Router, Request, Response } from 'express';
import { ProjectManager } from '../project/manager';
import { ApiResponse } from '../types';

export function createStatusRoutes(projectManager: ProjectManager): Router {
  const router = Router();

  router.get('/', (req: Request, res: Response): void => {
    try {
      const all = req.query.all === 'true';
      
      if (all) {
        const projects = projectManager.list();
        const status = projects.map(p => ({
          name: p.name,
          repo: p.repo,
          issues: 0,
          activeIssues: 0
        }));
        
        const response: ApiResponse = {
          success: true,
          data: status
        };
        res.json(response);
        return;
      }

      const current = projectManager.getCurrent();
      if (!current) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: crawlph project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      const status = {
        name: current.name,
        repo: current.repo,
        issues: 0,
        activeIssues: 0
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

  return router;
}
