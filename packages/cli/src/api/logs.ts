import { Hono } from 'hono';
import { ApiResponse } from '../types';
import { readLogTail } from '../util/log-tail';

export function createLogRoutes(): Hono {
  const app = new Hono();

  app.get('/tail', async (c) => {
    try {
      const cursorParam = c.req.query('cursor');
      const limitParam = c.req.query('limit');
      const maxBytesParam = c.req.query('maxBytes');

      let cursor: number | undefined;
      if (cursorParam != null && cursorParam !== '') {
        cursor = Number(cursorParam);
        if (!Number.isFinite(cursor) || cursor < 0) {
          const response: ApiResponse = {
            success: false,
            error: 'Invalid cursor: must be a non-negative number',
          };
          return c.json(response, 400);
        }
      }

      let limit: number | undefined;
      if (limitParam != null && limitParam !== '') {
        limit = Number(limitParam);
        if (!Number.isFinite(limit) || limit <= 0) {
          const response: ApiResponse = {
            success: false,
            error: 'Invalid limit: must be a positive number',
          };
          return c.json(response, 400);
        }
      }

      let maxBytes: number | undefined;
      if (maxBytesParam != null && maxBytesParam !== '') {
        maxBytes = Number(maxBytesParam);
        if (!Number.isFinite(maxBytes) || maxBytes <= 0) {
          const response: ApiResponse = {
            success: false,
            error: 'Invalid maxBytes: must be a positive number',
          };
          return c.json(response, 400);
        }
      }

      const result = await readLogTail({ cursor, limit, maxBytes });
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

  return app;
}
