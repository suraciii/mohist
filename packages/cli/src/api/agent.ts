import { Hono } from 'hono';
import { ApiResponse } from '../types';
import { AgentRunnerService } from '../services';

export function createAgentRoutes(agentRunner: AgentRunnerService): Hono {
  const app = new Hono();

  app.get('/status', async (c) => {
    const status = agentRunner.getStatus();

    const response: ApiResponse = {
      success: true,
      data: status
    };
    return c.json(response);
  });

  return app;
}
