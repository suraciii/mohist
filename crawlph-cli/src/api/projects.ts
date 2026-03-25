import { Router, Request, Response } from 'express';
import { StateManager } from '../server/state-manager';
import { ApiResponse, Project } from '../types';

export function createProjectRoutes(stateManager: StateManager): Router {
  const router = Router();

  router.post('/', (req: Request, res: Response): void => {
    try {
      const { name, path } = req.body;
      
      if (!name || !path) {
        const response: ApiResponse = {
          success: false,
          error: 'name and path are required'
        };
        res.status(400).json(response);
        return;
      }

      const existing = stateManager.getProjectByName(name);
      if (existing) {
        const response: ApiResponse = {
          success: false,
          error: 'Project with this name already exists'
        };
        res.status(409).json(response);
        return;
      }

      const project = stateManager.saveProject({ name, path });

      const response: ApiResponse<Project> = {
        success: true,
        data: project
      };
      res.status(201).json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.get('/', (_req: Request, res: Response): void => {
    try {
      const projects = stateManager.loadProjects();
      const response: ApiResponse<Project[]> = {
        success: true,
        data: projects
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

  router.get('/:name', (req: Request, res: Response): void => {
    try {
      const project = stateManager.getProjectByName(req.params.name);
      
      if (!project) {
        const response: ApiResponse = {
          success: false,
          error: 'Project not found'
        };
        res.status(404).json(response);
        return;
      }

      const response: ApiResponse<Project> = {
        success: true,
        data: project
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

  router.delete('/:name', (req: Request, res: Response): void => {
    try {
      const project = stateManager.getProjectByName(req.params.name);
      
      if (!project) {
        const response: ApiResponse = {
          success: false,
          error: 'Project not found'
        };
        res.status(404).json(response);
        return;
      }

      stateManager.deleteProject(project.id);

      const response: ApiResponse = {
        success: true
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

  router.post('/:name/use', (req: Request, res: Response): void => {
    try {
      const project = stateManager.getProjectByName(req.params.name);
      
      if (!project) {
        const response: ApiResponse = {
          success: false,
          error: 'Project not found'
        };
        res.status(404).json(response);
        return;
      }

      stateManager.setCurrentProjectId(project.id);

      const response: ApiResponse<Project> = {
        success: true,
        data: project
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
