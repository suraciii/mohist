import { Hono } from 'hono';
import { SkillService } from '../services/skill-service';
import { ProjectService } from '../services/project-service';
import { ApiResponse } from '../types';

export function createSkillRoutes(
  skillService: SkillService,
  projectService: ProjectService,
): Hono {
  const app = new Hono();

  app.get('/', async (c) => {
    try {
      const projectId = projectService.getCurrentId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>',
        };
        return c.json(response, 400);
      }

      const skills = skillService.getByProject(projectId);

      const response: ApiResponse = {
        success: true,
        data: skills.map((s) => ({
          id: s.id,
          name: s.name,
          description: s.description,
          createdAt: s.createdAt,
        })),
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error',
      };
      return c.json(response, 500);
    }
  });

  app.post('/:name/run', async (c) => {
    try {
      const projectId = projectService.getCurrentId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>',
        };
        return c.json(response, 400);
      }

      const skillName = c.req.param('name');
      const skill = skillService.getByName(skillName);

      if (!skill) {
        const response: ApiResponse = {
          success: false,
          error: `Skill not found: ${skillName}`,
        };
        return c.json(response, 404);
      }

      const project = projectService.getById(projectId);
      if (!project) {
        const response: ApiResponse = {
          success: false,
          error: 'Project not found',
        };
        return c.json(response, 400);
      }

      const run = skillService.run(skillName, projectId, project.path);

      const response: ApiResponse = {
        success: true,
        data: {
          runId: run.id,
          status: run.status,
          skillName: skill.name,
        },
      };
      return c.json(response, 202);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error',
      };
      return c.json(response, 500);
    }
  });

  app.get('/:name/runs', async (c) => {
    try {
      const skillName = c.req.param('name');
      const skill = skillService.getByName(skillName);

      if (!skill) {
        const response: ApiResponse = {
          success: false,
          error: `Skill not found: ${skillName}`,
        };
        return c.json(response, 404);
      }

      const runs = skillService.getRuns(skill.id);

      const response: ApiResponse = {
        success: true,
        data: runs.map((r) => ({
          ...r,
          output: r.output
            ? r.output.length > 500
              ? r.output.slice(0, 500) + '...'
              : r.output
            : null,
        })),
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error',
      };
      return c.json(response, 500);
    }
  });

  return app;
}
