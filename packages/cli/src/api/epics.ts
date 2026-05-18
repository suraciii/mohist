import { Hono } from 'hono';
import { ApiResponse, EpicPriority } from '../types';
import { EpicService, DuplicateEpicMembershipError, CrossProjectEpicMembershipError } from '../services';
import { ProjectService } from '../services';

export function createEpicRoutes(
  epicService: EpicService,
  _projectService: ProjectService,
): Hono {
  const app = new Hono();

  const getProjectId = (queryProjectId?: string): string | null => {
    return queryProjectId || _projectService.getCurrentId();
  };

  const noActiveProject = (): ApiResponse => ({
    success: false,
    error: 'No active project. Use: mo project use <name>',
  });

  app.get('/', async (c) => {
    try {
      const projectId = getProjectId(c.req.query('projectId'));
      if (!projectId) {
        return c.json(noActiveProject(), 400);
      }

      const epics = epicService.list(projectId);
      const response: ApiResponse = {
        success: true,
        data: epics,
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

  app.post('/', async (c) => {
    try {
      const projectId = getProjectId();
      if (!projectId) {
        return c.json(noActiveProject(), 400);
      }

      const { title, description, priority } = await c.req.json();

      if (!title || typeof title !== 'string' || title.trim().length === 0) {
        const response: ApiResponse = {
          success: false,
          error: 'title is required and must be a non-empty string',
          code: 'VALIDATION_ERROR',
          details: { field: 'title' },
        };
        return c.json(response, 400);
      }

      if (!description || typeof description !== 'string') {
        const response: ApiResponse = {
          success: false,
          error: 'description is required and must be a string',
          code: 'VALIDATION_ERROR',
          details: { field: 'description' },
        };
        return c.json(response, 400);
      }

      const validPriorities: EpicPriority[] = ['p0', 'p1', 'p2', 'p3', 'p4'];
      if (!priority || !validPriorities.includes(priority)) {
        const response: ApiResponse = {
          success: false,
          error: `priority is required and must be one of: ${validPriorities.join(', ')}`,
          code: 'VALIDATION_ERROR',
          details: { field: 'priority' },
        };
        return c.json(response, 400);
      }

      const epic = epicService.create({ projectId, title: title.trim(), description, priority });

      const response: ApiResponse = {
        success: true,
        data: epic,
      };
      return c.json(response, 201);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error',
      };
      return c.json(response, 500);
    }
  });

  app.get('/:id', async (c) => {
    try {
      const id = c.req.param('id');
      const projectId = getProjectId(c.req.query('projectId'));
      if (!projectId) {
        return c.json(noActiveProject(), 400);
      }

      const epic = epicService.getById(projectId, id);
      if (!epic) {
        const response: ApiResponse = {
          success: false,
          error: 'Epic not found',
        };
        return c.json(response, 404);
      }

      const response: ApiResponse = {
        success: true,
        data: epic,
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

  app.post('/:id/issues', async (c) => {
    let requestedIssueId: string | undefined;
    try {
      const id = c.req.param('id');
      const projectId = getProjectId();
      if (!projectId) {
        return c.json(noActiveProject(), 400);
      }
      const { issueId } = await c.req.json();
      requestedIssueId = issueId;

      if (!issueId || typeof issueId !== 'string') {
        const response: ApiResponse = {
          success: false,
          error: 'issueId is required and must be a string',
        };
        return c.json(response, 400);
      }

      epicService.addIssue(projectId, id, issueId);

      const response: ApiResponse = {
        success: true,
        data: { epicId: id, issueId },
      };
      return c.json(response, 201);
    } catch (error) {
      if (error instanceof DuplicateEpicMembershipError) {
        const response: ApiResponse = {
          success: false,
          error: error.message,
          code: 'DUPLICATE_EPIC_MEMBERSHIP',
          details: {
            issueId: error.issueId,
            existingEpicId: error.existingEpicId,
            existingEpicTitle: error.existingEpicTitle,
          },
        };
        return c.json(response, 409);
      }
      if (error instanceof Error && error.message === 'Epic not found') {
        const response: ApiResponse = {
          success: false,
          error: 'Epic not found',
        };
        return c.json(response, 404);
      }
      if (error instanceof Error && error.message === 'Issue not found') {
        const response: ApiResponse = {
          success: false,
          error: 'Issue not found',
          code: 'ISSUE_NOT_FOUND',
          details: { issueId: requestedIssueId },
        };
        return c.json(response, 404);
      }
      if (error instanceof CrossProjectEpicMembershipError) {
        const response: ApiResponse = {
          success: false,
          error: error.message,
          code: 'CROSS_PROJECT_EPIC_MEMBERSHIP',
          details: {
            issueId: error.issueId,
            epicProjectId: error.epicProjectId,
            issueProjectId: error.issueProjectId,
          },
        };
        return c.json(response, 409);
      }
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error',
      };
      return c.json(response, 500);
    }
  });

  app.delete('/:id/issues/:issueId', async (c) => {
    try {
      const id = c.req.param('id');
      const issueId = c.req.param('issueId');
      const projectId = getProjectId();
      if (!projectId) {
        return c.json(noActiveProject(), 400);
      }

      epicService.removeIssue(projectId, id, issueId);

      const response: ApiResponse = {
        success: true,
        data: { epicId: id, issueId },
      };
      return c.json(response);
    } catch (error) {
      if (error instanceof Error && error.message === 'Epic not found') {
        const response: ApiResponse = {
          success: false,
          error: 'Epic not found',
        };
        return c.json(response, 404);
      }
      if (error instanceof Error && error.message === 'Issue is not linked to this Epic') {
        const response: ApiResponse = {
          success: false,
          error: 'Issue is not linked to this Epic',
        };
        return c.json(response, 404);
      }
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error',
      };
      return c.json(response, 500);
    }
  });

  app.post('/:id/done', async (c) => {
    try {
      const id = c.req.param('id');
      const projectId = getProjectId();
      if (!projectId) {
        return c.json(noActiveProject(), 400);
      }

      const epic = epicService.markDone(projectId, id);
      if (!epic) {
        const response: ApiResponse = {
          success: false,
          error: 'Epic not found',
        };
        return c.json(response, 404);
      }

      const response: ApiResponse = {
        success: true,
        data: epic,
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error',
      };
      return c.json(response, 400);
    }
  });

  app.post('/:id/close', async (c) => {
    try {
      const id = c.req.param('id');
      const projectId = getProjectId();
      if (!projectId) {
        return c.json(noActiveProject(), 400);
      }

      const epic = epicService.close(projectId, id);
      if (!epic) {
        const response: ApiResponse = {
          success: false,
          error: 'Epic not found',
        };
        return c.json(response, 404);
      }

      const response: ApiResponse = {
        success: true,
        data: epic,
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error',
      };
      return c.json(response, 400);
    }
  });

  return app;
}
