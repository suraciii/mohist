import { Hono } from 'hono';
import { ProjectService } from '../services/project-service';
import { IssueService } from '../services/issue-service';
import { ApiResponse } from '../types';
import { resolveModel, type LlmConfig } from '../agent-runtime';

export function createStatusRoutes(
  projectService: ProjectService,
  issueService: IssueService,
  llmConfig?: LlmConfig
): Hono {
  const app = new Hono();

  app.get('/status', async (c) => {
    try {
      const all = c.req.query('all') === 'true';
      
      if (all) {
        const projects = projectService.getAll();
        const currentId = projectService.getCurrentId();
        const status = projects.map(p => {
          const issues = issueService.getByProject(p.id);
          const activeIssues = issues.filter(i => i.status === 'active').length;
          
          return {
            name: p.name,
            path: p.path,
            issues: issues.length,
            activeIssues,
            isCurrent: currentId === p.id
          };
        });
        
        const response: ApiResponse = {
          success: true,
          data: status
        };
        return c.json(response);
      }

      const currentId = projectService.getCurrentId();
      if (!currentId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const current = projectService.getById(currentId);
      if (!current) {
        const response: ApiResponse = {
          success: false,
          error: 'Current project not found'
        };
        return c.json(response, 404);
      }

      const issues = issueService.getByProject(currentId);
      const activeIssues = issues.filter(i => i.status === 'active');

      let llm: { configured: false; provider?: undefined; model?: undefined } | { configured: true; provider: string; model: string } = { configured: false };
      try {
        await resolveModel(llmConfig);
        const modelStr = llmConfig?.model ?? 'anthropic/claude-sonnet-4-20250514';
        const slashIndex = modelStr.indexOf('/');
        if (slashIndex !== -1) {
          llm = {
            configured: true,
            provider: modelStr.slice(0, slashIndex),
            model: modelStr.slice(slashIndex + 1),
          };
        }
      } catch {
        // LLM not configured or invalid - llm stays { configured: false }
      }

      const status = {
        name: current.name,
        path: current.path,
        issues: issues.length,
        activeIssues: activeIssues.length,
        issuesByStage: {
          draft: issues.filter(i => i.stage === 'draft').length,
          plan: issues.filter(i => i.stage === 'plan').length,
          build: issues.filter(i => i.stage === 'build').length,
          check: issues.filter(i => i.stage === 'check').length,
          done: issues.filter(i => i.stage === 'done').length,
        },
        llm,
      };

      const response: ApiResponse = {
        success: true,
        data: status
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

  app.get('/health', async (c) => {
    return c.json({ status: 'ok', timestamp: new Date().toISOString() });
  });

  return app;
}
