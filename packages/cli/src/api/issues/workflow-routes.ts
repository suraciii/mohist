import { Hono } from 'hono';
import type { IssueService, ProjectService } from '../../services';
import { WorkflowRuntime } from '@mohist/workflow';
import { WorkflowStoreAdapter } from '../../workflow/runtime/store';
import { createMohistTaskHandlers, createMohistCheckHandlers, createMohistTaskLoaders } from '../../workflow/runtime/handlers';
import { IssueWorkflowService, type IssueWorkflowResult } from '../../issue/issue-workflow-service';

const toResponse = (c: any, r: IssueWorkflowResult) =>
  r.ok ? c.json(r.data) : c.json({ error: r.error }, 400);

export function createWorkflowRoutes(
  issueService: IssueService,
  projectService: ProjectService,
  getDatabaseManager: () => import('../../db/database').DatabaseManager,
): Hono {
  const app = new Hono();
  const svc = new IssueWorkflowService({
    issueService,
    projectService,
    runtime: new WorkflowRuntime({
      store: new WorkflowStoreAdapter(getDatabaseManager()),
      tasks: createMohistTaskHandlers({ worktreePath: '', issue: null as never, projectId: '' }),
      checks: createMohistCheckHandlers({ worktreePath: '', issue: null as never, projectId: '' }),
      taskLoaders: createMohistTaskLoaders({ worktreePath: '', issue: null as never, projectId: '' }),
    }),
  });

  app.post('/:number/start', async (c) => toResponse(c, await svc.start(+c.req.param('number'))));
  app.post('/:number/force-stop', async (c) => toResponse(c, await svc.stop(+c.req.param('number'))));
  app.post('/:number/resume', async (c) => toResponse(c, await svc.resume(+c.req.param('number'))));
  app.post('/:number/approve', async (c) => toResponse(c, await svc.approve(+c.req.param('number'))));
  app.post('/:number/reject', async (c) => {
    const body = await c.req.json<{ message?: string }>().catch(() => ({}) as any);
    return toResponse(c, await svc.reject(+c.req.param('number'), body.message));
  });
  app.post('/:number/retry', async (c) => toResponse(c, await svc.retry(+c.req.param('number'))));
  app.post('/:number/rerun', async (c) => toResponse(c, await svc.rerun(+c.req.param('number'))));
  app.post('/:number/rebase', async (c) => toResponse(c, await svc.rebase(+c.req.param('number'))));
  app.get('/:number/logs', (c) => toResponse(c, svc.logs(+c.req.param('number'), c.req.query('eventType') ?? undefined)));

  return app;
}
