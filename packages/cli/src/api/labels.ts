import { Hono } from 'hono';
import { ProjectService } from '../services/project-service';
import { ApiResponse } from '../types';

export function createLabelRoutes(projectService: ProjectService): Hono {
  const app = new Hono();

  app.get('/', async (c) => {
    try {
      const projectId = projectService.getCurrentId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const labels = projectService.getLabels(projectId);

      const response: ApiResponse<string[]> = {
        success: true,
        data: labels
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

  return app;
}
