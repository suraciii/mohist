import { Hono, type Context } from 'hono';
import type { ContentfulStatusCode } from 'hono/utils/http-status';
import type { StateManager } from '../../server/state-manager';
import { ApiResponse } from '../../types';
import type { IssueService, ProjectService, AgentRunnerService } from '../../services';
import type { ConflictResolutionDeps } from '../../services/conflict-resolution';
import type { WorktreeManager } from '../../git/worktree-manager';
import type { LlmConfig } from '../../agent-runtime';
import type { WorkflowLogRepo } from '../../db/workflow-log-repo';
import type { SessionStreamLogRepo } from '../../db/session-stream-log-repo';
import type { CoderSessionRepo } from '../../db/coder-session-repo';
import type { PipelineCheckpointRepo } from '../../db/pipeline-checkpoint-repo';
import type { CheckSuiteRepo } from '../../db/check-suite-repo';
import type { StageExecutionRepo } from '../../db/stage-execution-repo';
import type { IssuePrerequisiteService } from '../../services/issue-prerequisite-service';
import type { EpicService } from '../../services/epic-service';
import type { WorkflowRunService } from '../../services/workflow-run-service';
import { IssueWorkflowService, type IssueWorkflowResult } from '../../services/issue-workflow-service';

type StageStateService = unknown;

export function createWorkflowRoutes(
  issueService: IssueService,
  projectService: ProjectService,
  stateManager: StateManager,
  worktreeManager: WorktreeManager | null = null,
  _llmConfig?: LlmConfig,
  agentRunner?: AgentRunnerService,
  workflowLogRepo?: WorkflowLogRepo,
  _sessionStreamLogRepo?: SessionStreamLogRepo,
  coderSessionRepo?: CoderSessionRepo,
  _opencodeBinPath?: string,
  checkpointRepo?: PipelineCheckpointRepo,
  _resolveConflictsDeps?: ConflictResolutionDeps,
  checkSuiteRepo?: CheckSuiteRepo,
  stageExecutionRepo?: StageExecutionRepo,
  stageStateService?: StageStateService,
  workflowRunService?: WorkflowRunService,
  issuePrerequisiteService?: IssuePrerequisiteService,
  _epicService?: EpicService,
): Hono {
  const app = new Hono();
  void coderSessionRepo;
  void checkpointRepo;
  void checkSuiteRepo;
  void stageExecutionRepo;
  void stageStateService;
  void workflowRunService;

  const workflowService = new IssueWorkflowService({
    issueService,
    projectService,
    worktreeManager,
    agentRunner,
    workflowLogRepo,
    issuePrerequisiteService,
    getIssueRepo: () => stateManager.getIssueRepo(),
  });

  const send = (c: Context, result: IssueWorkflowResult) => {
    const body: ApiResponse = result.ok
      ? { success: true, data: result.data }
      : { success: false, error: result.error, ...(result.data === undefined ? {} : { data: result.data }) };
    if (result.status) return c.json(body, result.status as ContentfulStatusCode);
    return c.json(body);
  };

  const fail = (error: unknown): IssueWorkflowResult => ({
    ok: false,
    error: error instanceof Error ? error.message : 'Unknown error',
    status: 500,
  });

  const parseNumber = (raw: string): number => Number.parseInt(raw, 10);
  const readJson = async (c: Context) => {
    return await c.req.json().catch(() => ({} as Record<string, unknown>));
  };

  app.post('/:number/start', (c) => send(c, workflowService.start(parseNumber(c.req.param('number')))));
  app.post('/:number/force-stop', (c) => send(c, workflowService.stop(parseNumber(c.req.param('number')))));
  app.post('/:number/resume', (c) => send(c, workflowService.resume(parseNumber(c.req.param('number')))));
  app.post('/:number/approve', (c) => send(c, workflowService.approve(parseNumber(c.req.param('number')))));

  app.post('/:number/reject', async (c) => {
    try {
      const body = await readJson(c);
      return send(c, workflowService.reject(parseNumber(c.req.param('number')), body.message as string | undefined));
    } catch (error) {
      return send(c, fail(error));
    }
  });

  app.post('/:number/messages', async (c) => {
    try {
      const body = await readJson(c);
      return send(c, workflowService.message(parseNumber(c.req.param('number')), body.message));
    } catch (error) {
      return send(c, fail(error));
    }
  });

  app.get('/:number/logs', (c) => send(c, workflowService.logs(parseNumber(c.req.param('number')), c.req.query('eventType'))));

  app.post('/:number/rebase', async (c) => {
    try {
      await readJson(c);
      return send(c, workflowService.rebase(parseNumber(c.req.param('number'))));
    } catch (error) {
      return send(c, fail(error));
    }
  });

  app.post('/:number/retry', (c) => send(c, workflowService.retry(parseNumber(c.req.param('number')))));
  app.post('/:number/rerun', async (c) => {
    try {
      const body = await readJson(c);
      return send(c, workflowService.rerun(parseNumber(c.req.param('number')), body.stage as never));
    } catch (error) {
      return send(c, fail(error));
    }
  });

  return app;
}
