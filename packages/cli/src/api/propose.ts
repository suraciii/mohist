import { Hono } from 'hono';
import { StateManager } from '../server/state-manager';
import { ApiResponse, Stage } from '../types';
import { IssueService } from '../services';
import { ProjectService } from '../services';
import { AgentRunnerService } from '../services';
import { WorktreeManager } from '../git/worktree-manager';
import { SessionManager, type LlmConfig } from '../agent-runtime';
import { createChange } from '../openspec/change-creator';

export function createProposeRoutes(
  issueService: IssueService,
  projectService: ProjectService,
  stateManager: StateManager,
  worktreeManager: WorktreeManager | null = null,
  sessionManager: SessionManager = new SessionManager(),
  llmConfig?: LlmConfig,
  agentRunner?: AgentRunnerService,
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
          worktreePath = await worktreeManager.create(project.path, project.name, issue.number);
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
      const updatedIssue = issueService.getByNumber(projectId, number);

      if (!agentRunner) {
        const response: ApiResponse = {
          success: false,
          error: 'AgentRunnerService not configured'
        };
        return c.json(response, 500);
      }

      if (agentRunner.isRunning(updatedIssue!.id)) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} already has an agent running`
        };
        return c.json(response, 409);
      }

      const startResult = agentRunner.start(
        updatedIssue!,
        projectId,
        stateManager.getIssueRepo(),
        stateManager.getCommentRepo(),
        stateManager.getQuestionRepo(),
        worktreePath,
        sessionManager,
        llmConfig,
        (issueId, status) => issueService.setStatus(issueId, status),
      );

      if (startResult.started) {
        const response: ApiResponse = {
          success: true,
          data: {
            issue: updatedIssue,
            changePath: changeResult.changePath,
            changeName: changeResult.changeName,
            isNew: changeResult.isNew,
            message: `Issue #${number} proposed, Change "${changeResult.changeName}" created, agent is running`
          }
        };
        return c.json(response);
      }

      const response: ApiResponse = {
        success: true,
        data: {
          issue: updatedIssue,
          changePath: changeResult.changePath,
          changeName: changeResult.changeName,
          isNew: changeResult.isNew,
          message: `Issue #${number} proposed, Change "${changeResult.changeName}" created, queued at position ${startResult.queuePosition}`,
          queuePosition: startResult.queuePosition,
          runningAgents: agentRunner.getStatus().activeAgents.length,
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