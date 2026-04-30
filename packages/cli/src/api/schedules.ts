import { Hono } from 'hono';
import { ApiResponse } from '../types';
import { SchedulerService } from '../services';
import { ScheduleRepo } from '../db/schedule-repo';

export function createScheduleRoutes(
  scheduleRepo: ScheduleRepo,
  schedulerService: SchedulerService,
  projectPath?: string,
): Hono {
  const app = new Hono();

  app.get('/', async (c) => {
    try {
      const schedules = scheduleRepo.getAll();

      const response: ApiResponse = {
        success: true,
        data: schedules,
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

  app.post('/refresh', async (c) => {
    try {
      const result = schedulerService.refreshSchedules(projectPath);

      const response: ApiResponse = {
        success: true,
        data: result,
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

  app.patch('/:skillId', async (c) => {
    try {
      const skillId = c.req.param('skillId');
      const body = await c.req.json<{ enabled?: boolean }>();

      if (body.enabled === undefined) {
        const response: ApiResponse = {
          success: false,
          error: 'enabled field is required',
        };
        return c.json(response, 400);
      }

      const existing = scheduleRepo.getBySkillId(skillId);
      if (!existing) {
        const response: ApiResponse = {
          success: false,
          error: `Schedule not found for skill: ${skillId}`,
        };
        return c.json(response, 404);
      }

      const updated = body.enabled
        ? schedulerService.enableSchedule(skillId)
        : schedulerService.disableSchedule(skillId);

      const response: ApiResponse = {
        success: true,
        data: updated,
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
