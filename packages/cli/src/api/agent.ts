import { Hono } from 'hono';
import { ApiResponse } from '../types';
import { AgentRunnerService, ProjectService } from '../services';
import { CoderSessionRepo } from '../db/coder-session-repo';

export interface SessionStatus {
  sessionId: string | null;
  acpSessionId: string | null;
  status: string | null;
  currentSessionState: 'Running' | 'Checking session' | 'Session failed' | 'No active session';
  lastDataAt: string | null;
  probeSentAt: string | null;
  probeDeadlineAt: string | null;
  failureReason: string | null;
}

export function createAgentRoutes(
  agentRunner: AgentRunnerService,
  coderSessionRepo?: CoderSessionRepo,
  projectService?: ProjectService,
): Hono {
  const app = new Hono();

  const noActiveSession = (): SessionStatus => ({
    sessionId: null,
    acpSessionId: null,
    status: null,
    currentSessionState: 'No active session',
    lastDataAt: null,
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: null,
  });

  const currentSessionState = (status: string | null): SessionStatus['currentSessionState'] => {
    if (status === 'failed') return 'Session failed';
    if (status === 'probing') return 'Checking session';
    if (status === 'running') return 'Running';
    return 'No active session';
  };

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

  app.get('/session-status', async (c) => {
    const projectId = projectService?.getCurrentId();
    if (!projectId) {
      return c.json({
        success: true,
        data: noActiveSession(),
      });
    }

    const activeSession = coderSessionRepo?.findLatestCurrentByProjectId(projectId) ?? null;

    if (!activeSession) {
      return c.json({
        success: true,
        data: noActiveSession(),
      });
    }

    const data: SessionStatus = {
      sessionId: activeSession.id,
      acpSessionId: activeSession.acpSessionId,
      status: activeSession.status,
      currentSessionState: currentSessionState(activeSession.status),
      lastDataAt: activeSession.lastDataAt,
      probeSentAt: activeSession.probeSentAt,
      probeDeadlineAt: activeSession.probeDeadlineAt,
      failureReason: activeSession.failureReason,
    };

    return c.json({ success: true, data });
  });

  return app;
}
