import { Hono } from 'hono';
import { ApiResponse } from '../types';
import { AgentRunnerService, ProjectService } from '../services';
import { CoderSessionRepo } from '../db/coder-session-repo';

export function createAgentRoutes(
  agentRunner: AgentRunnerService,
  coderSessionRepo?: CoderSessionRepo,
  projectService?: ProjectService,
): Hono {
  const app = new Hono();

  app.get('/status', async (c) => {
    const status = agentRunner.getStatus();

    const response: ApiResponse = {
      success: true,
      data: status
    };
    return c.json(response);
  });

  app.get('/sessions', async (c) => {
    const projectId = projectService?.getCurrentId();
    if (!projectId) {
      return c.json({ success: true, data: [] });
    }

    const status = c.req.query('status');
    const limitParam = c.req.query('limit');
    const limit = limitParam ? parseInt(limitParam, 10) : 50;

    const sessions = coderSessionRepo!.findAllWithIssueInfo(projectId, status, limit);

    return c.json({
      success: true,
      data: sessions,
    });
  });

  return app;
}
