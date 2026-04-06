import { Hono } from 'hono';
import { ProjectService } from '../services/project-service';
import { ApiResponse, Project } from '../types';

export function createProjectRoutes(projectService: ProjectService): Hono {
  const app = new Hono();

  app.post('/', async (c) => {
    try {
      const { name, path } = await c.req.json();

      if (!name || !path) {
        const response: ApiResponse = {
          success: false,
          error: 'name and path are required'
        };
        return c.json(response, 400);
      }

      const existing = projectService.getByName(name);
      if (existing) {
        const response: ApiResponse = {
          success: false,
          error: 'Project with this name already exists'
        };
        return c.json(response, 409);
      }

      const project = projectService.create({ name, path });

      const response: ApiResponse<Project> = {
        success: true,
        data: project
      };
      return c.json(response, 201);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.get('/', async (c) => {
    try {
      const projects = projectService.getAll();
      const response: ApiResponse<Project[]> = {
        success: true,
        data: projects
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

  app.get('/:name', async (c) => {
    try {
      const project = projectService.getByName(c.req.param('name'));

      if (!project) {
        const response: ApiResponse = {
          success: false,
          error: 'Project not found'
        };
        return c.json(response, 404);
      }

      const response: ApiResponse<Project> = {
        success: true,
        data: project
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

  app.delete('/:name', async (c) => {
    try {
      const project = projectService.getByName(c.req.param('name'));

      if (!project) {
        const response: ApiResponse = {
          success: false,
          error: 'Project not found'
        };
        return c.json(response, 404);
      }

      projectService.delete(project.id);

      const response: ApiResponse = {
        success: true
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

  app.post('/:name/use', async (c) => {
    try {
      const project = projectService.getByName(c.req.param('name'));

      if (!project) {
        const response: ApiResponse = {
          success: false,
          error: 'Project not found'
        };
        return c.json(response, 404);
      }

      projectService.setCurrent(project);

      const response: ApiResponse<Project> = {
        success: true,
        data: project
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
