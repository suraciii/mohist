import { Hono } from 'hono';
import { StateManager } from '../server/state-manager';
import { ApiResponse, IssueStatus, Stage } from '../types';
import { IssueService } from '../services';
import { ProjectService } from '../services';
import { AgentRunnerService } from '../services';
import { WorktreeManager } from '../git/worktree-manager';
import { createChange } from '../openspec/change-creator';

export function createProposeRoutes(
  issueService: IssueService,
  projectService: ProjectService,
  _stateManager: StateManager,
  worktreeManager: WorktreeManager | null = null,
  _llmConfig?: unknown,
  agentRunner?: AgentRunnerService,
  _opencodeBinPath?: string,
): Hono {
  const app = new Hono();

  const getCurrentProjectId = (): string | null => {
    return projectService.getCurrentId();
  };

  app.post('/:number/propose', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const { force } = await c.req.json().catch(() => ({ force: false }));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      if (issue.status !== IssueStatus.Active) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is ${issue.status}. Run: mo issue reopen ${number} to reactivate`
        };
        return c.json(response, 409);
      }

      const project = projectService.getById(projectId);
      if (!project) {
        const response: ApiResponse = {
          success: false,
          error: 'Project not found'
        };
        return c.json(response, 404);
      }

      let worktreePath = project.path;
      if (worktreeManager) {
        if (worktreeManager.exists(project.name, issue.number)) {
          worktreePath = worktreeManager.getPath(project.name, issue.number) || project.path;
        } else {
          worktreePath = await worktreeManager.create(project.path, project.name, issue.number, project.baseBranch);
        }
      }

      const changeResult = createChange(
        worktreePath,
        issue.number,
        issue.title,
        issue.id,
        force
      );

      issueService.transitionToStage(issue.id, Stage.Plan);

      if (!agentRunner) {
        const response: ApiResponse = {
          success: false,
          error: 'AgentRunnerService not configured'
        };
        return c.json(response, 500);
      }

      const result = agentRunner.enqueue(issue.id, 'start-pipeline');

      const response: ApiResponse = {
        success: true,
        data: {
          issue: issueService.getByNumber(projectId, number),
          changePath: changeResult.changePath,
          changeName: changeResult.changeName,
          isNew: changeResult.isNew,
          taskId: result.taskId,
          status: result.status,
          queuePosition: result.queuePosition,
          message: `Issue #${number} proposed, Change "${changeResult.changeName}" created, enqueued for start-pipeline`
        }
      };
      return c.json(response, 202);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  return app;
}
