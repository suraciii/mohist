import { Hono } from 'hono';
import * as fs from 'fs';
import * as path from 'path';
import { StateManager } from '../server/state-manager';
import { ApiResponse, Issue, Stage, IssueStatus, Comment, Priority, VALID_PRIORITIES } from '../types';
import { IssueService } from '../services';
import { ProjectService } from '../services';
import { AgentRunnerService } from '../services';
import { WorktreeManager } from '../git/worktree-manager';
import { MergeQueue } from '../git/merge-queue';
import type { LlmConfig } from '../agent-runtime';
import type { AcpConnectionOptions } from '../agent-runtime/acp-session';
import { WorkflowLogRepo } from '../db/workflow-log-repo';
import { AgentSessionMessageRepo } from '../db/agent-session-message-repo';
import { CoderSessionRepo } from '../db/coder-session-repo';
import { PipelineCheckpointRepo } from '../db/pipeline-checkpoint-repo';
import { detectOpenSpecChange, findChangeDir } from '../openspec/detector';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { Log } from '../util/log';
import { eventBus } from '../services/event-bus';

const log = Log.create({ service: 'issue' });

const execFileAsync = promisify(execFile);

export function createIssueRoutes(
  issueService: IssueService,
  projectService: ProjectService,
  stateManager: StateManager,
  worktreeManager: WorktreeManager | null = null,
  _sessionManager?: unknown,
  _llmConfig?: LlmConfig,
  agentRunner?: AgentRunnerService,
  workflowLogRepo?: WorkflowLogRepo,
  agentSessionMessageRepo?: AgentSessionMessageRepo,
  coderSessionRepo?: CoderSessionRepo,
  opencodeBinPath?: string,
  mergeQueue?: MergeQueue,
  checkpointRepo?: PipelineCheckpointRepo,
): Hono {
  const app = new Hono();

  const getCurrentProjectId = (): string | null => {
    return projectService.getCurrentId();
  };

  app.get('/', async (c) => {
    try {
      const projectId = c.req.query('projectId') || getCurrentProjectId();
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const stage = c.req.query('stage') as Stage | undefined;
      const label = c.req.query('label') as string | undefined;
      const priority = c.req.query('priority') as string | undefined;

      if (priority && !VALID_PRIORITIES.includes(priority as Priority)) {
        const response: ApiResponse = {
          success: false,
          error: 'Invalid priority'
        };
        return c.json(response, 400);
      }
      
      let issues = stage 
        ? issueService.getByStage(projectId, stage)
        : issueService.getByProject(projectId);

      if (priority) {
        issues = issues.filter(issue => issue.priority === priority);
      }
      
      if (label) {
        issues = issues.filter(issue => issue.labels.includes(label));
      }

      const project = projectService.getById(projectId);
      const issuesWithProject = issues.map(issue => ({
        ...issue,
        projectName: project?.name || 'unknown'
      }));

      const response: ApiResponse = {
        success: true,
        data: issuesWithProject
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

  app.post('/', async (c) => {
    try {
      const { title, body, labels, priority } = await c.req.json();
      
      if (!title) {
        const response: ApiResponse = {
          success: false,
          error: 'title is required'
        };
        return c.json(response, 400);
      }

      if (priority !== undefined && !VALID_PRIORITIES.includes(priority as Priority)) {
        const response: ApiResponse = {
          success: false,
          error: 'Invalid priority'
        };
        return c.json(response, 400);
      }

      const projectId = getCurrentProjectId();
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project'
        };
        return c.json(response, 400);
      }

      const issue = issueService.create({ projectId, title, body, labels, priority });

      const response: ApiResponse<Issue> = {
        success: true,
        data: issue
      };
      return c.json(response, 201);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.get('/merge-queue/status', async (c) => {
    try {
      const projectId = getCurrentProjectId();
      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      if (!mergeQueue) {
        return c.json({ success: true, data: { items: [] } } satisfies ApiResponse);
      }

      const entries = mergeQueue.getStatus().filter(e => e.projectId === projectId);
      return c.json({ success: true, data: { items: entries } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.get('/:number', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: mo project use <name>'
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

      const comments = issueService.getCommentsByIssue(issue.id);
      const project = projectService.getById(projectId);

      const response: ApiResponse = {
        success: true,
        data: {
          ...issue,
          projectName: project?.name || 'unknown',
          projectPath: project?.path || '',
          baseBranch: project?.baseBranch || 'main',
          comments
        }
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

  app.patch('/:number', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
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

      const { title, body, addLabels, removeLabels, priority } = await c.req.json();

      if (priority !== undefined && !VALID_PRIORITIES.includes(priority as Priority)) {
        const response: ApiResponse = {
          success: false,
          error: 'Invalid priority'
        };
        return c.json(response, 400);
      }

      const updateData: Partial<{ title: string; body: string; labels: string[]; priority: Priority }> = {};
      
      if (title !== undefined) updateData.title = title;
      if (body !== undefined) updateData.body = body;
      if (priority !== undefined) updateData.priority = priority;
      
      if (addLabels || removeLabels) {
        let currentLabels = [...issue.labels];
        
        if (addLabels && Array.isArray(addLabels)) {
          currentLabels = [...new Set([...currentLabels, ...addLabels])];
        }
        
        if (removeLabels && Array.isArray(removeLabels)) {
          currentLabels = currentLabels.filter(l => !removeLabels.includes(l));
        }
        
        updateData.labels = currentLabels;
      }

      issueService.update(issue.id, updateData);
      const updated = issueService.getByNumber(projectId, number);

      const response: ApiResponse<Issue> = {
        success: true,
        data: updated || undefined
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

  app.post('/:number/comments', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const { body } = await c.req.json();
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      if (!body) {
        const response: ApiResponse = {
          success: false,
          error: 'body is required'
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

      const comment = issueService.createComment(issue.id, body);

      const response: ApiResponse<Comment> = {
        success: true,
        data: comment
      };
      return c.json(response, 201);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.post('/:number/start', async (c) => {
    let stageTransitioned = false;
    try {
      const number = parseInt(c.req.param('number'));
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

      if (issue.status === IssueStatus.Blocked) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is blocked. Run: mo issue reopen ${number}`
        };
        return c.json(response, 400);
      }

      if (issue.status === IssueStatus.Closed) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is closed. Run: mo issue reopen ${number}`
        };
        return c.json(response, 400);
      }

      if (issue.status === IssueStatus.Paused) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is paused. Run: mo issue approve ${number} to resume`
        };
        return c.json(response, 400);
      }

      if (issue.stage !== Stage.Draft) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is not in draft stage (current: ${issue.stage}). Only draft issues can be started.`
        };
        return c.json(response, 400);
      }

      if (!agentRunner) {
        const response: ApiResponse = {
          success: false,
          error: 'AgentRunnerService not configured'
        };
        return c.json(response, 500);
      }

      const project = projectService.getById(projectId);
      let worktreePath = process.cwd();
      if (worktreeManager && project) {
        worktreePath = await worktreeManager.create(project.path, project.name, issue.number, project.baseBranch);
      }

      issueService.transitionToStage(issue.id, Stage.Plan);
      stageTransitioned = true;
      const updatedIssue = issueService.getByNumber(projectId, number)!;

      if (agentRunner.isRunning(updatedIssue.id)) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} already has an agent running`
        };
        return c.json(response, 409);
      }

      const status = agentRunner.getStatus();
      if (status.activeAgents.length >= agentRunner.getMaxConcurrentAgents()) {
        const response: ApiResponse = {
          success: false,
          error: `Concurrent agent limit reached (${agentRunner.getMaxConcurrentAgents()})`
        };
        return c.json(response, 429);
      }

      const acpOptions: AcpConnectionOptions = {
        cwd: worktreePath,
        issueId: updatedIssue.id,
        projectId,
        workflowLogRepo,
        coderSessionRepo,
        eventBus,
        issueNumber: updatedIssue.number,
        opencodeBinPath,
      };

      const startResult = agentRunner.startPipeline(
        updatedIssue,
        projectId,
        stateManager.getIssueRepo(),
        worktreePath,
        acpOptions,
        (issueId, status) => issueService.setStatus(issueId, status),
      );

      if (startResult.started) {
        const response: ApiResponse = {
          success: true,
          data: {
            issue: updatedIssue,
            message: `Issue #${number} started, pipeline is running`,
            runningAgents: agentRunner.getStatus().activeAgents.length,
          }
        };
        return c.json(response);
      }

      const response: ApiResponse = {
        success: false,
        error: startResult.error ?? `Issue #${number} could not be started`,
      };
      return c.json(response, 409);
    } catch (error) {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      const project = projectId ? projectService.getById(projectId) : null;

      if (stageTransitioned) {
        try {
          const issue = projectId ? issueService.getByNumber(projectId, number) : null;
          if (issue && issue.stage === Stage.Plan) {
            issueService.transitionToStage(issue.id, Stage.Draft);
          }
        } catch (rollbackError) {
          log.error('Failed to rollback stage to Draft', { error: rollbackError instanceof Error ? rollbackError.message : rollbackError });
        }
      } else if (worktreeManager && project) {
        try {
          await worktreeManager.remove(project.path, project.name, number);
        } catch (cleanupError) {
          log.error('Failed to cleanup worktree after start failure', {
            issueNumber: number,
            error: cleanupError instanceof Error ? cleanupError.message : String(cleanupError),
          });
        }
      }

      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.post('/:number/stop', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
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

      if (!agentRunner) {
        const response: ApiResponse = {
          success: false,
          error: 'AgentRunnerService not configured'
        };
        return c.json(response, 500);
      }

      if (!agentRunner.isRunning(issue.id)) {
        const response: ApiResponse = {
          success: false,
          error: `No agent running for issue #${number}`
        };
        return c.json(response, 409);
      }

      const stopped = await agentRunner.stop(issue.id);
      if (!stopped) {
        const response: ApiResponse = {
          success: false,
          error: `Failed to stop agent for issue #${number}`
        };
        return c.json(response, 500);
      }

      const response: ApiResponse = {
        success: true,
        data: { message: `Agent for issue #${number} stopped` }
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

  app.post('/:number/close', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
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

      if (agentRunner && agentRunner.isRunning(issue.id)) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} has an agent running. Wait for it to complete or pause first.`
        };
        return c.json(response, 409);
      }

      const { cleanup } = await c.req.json().catch(() => ({ cleanup: false }));

      const closedIssue = issueService.close(projectId, number);
      if (!closedIssue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      if (cleanup && worktreeManager) {
        const project = projectService.getById(projectId);
        if (project) {
          await worktreeManager.remove(project.path, project.name, number).catch((err) => {
            log.warn('Failed to cleanup worktree on close', { number, error: err instanceof Error ? err.message : err });
          });
        }
      }

      const response: ApiResponse = {
        success: true,
        data: { issue: closedIssue, message: `Issue #${number} closed` }
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

  app.post('/:number/reopen', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      const issue = issueService.reopen(projectId, number);
      
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found or not reopenable (current status must be closed, blocked, or paused)`
        };
        return c.json(response, 404);
      }

      if (agentRunner && agentRunner.isRunning(issue.id)) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} already has an agent running. Wait for it to complete first.`
        };
        return c.json(response, 409);
      }

      const project = projectService.getById(projectId);
      let worktreePath = process.cwd();
      if (worktreeManager && project) {
        const existingPath = worktreeManager.getPath(project.name, issue.number);
        worktreePath = existingPath || process.cwd();
      }

      let isReviewRecovery = false;
      if (issue.stage === Stage.Review) {
        const changeDir = findChangeDir(worktreePath, issue.number);
        if (changeDir) {
          try {
            const tasksPath = path.join(changeDir, 'tasks.json');
            const tasksContent = fs.readFileSync(tasksPath, 'utf-8');
            const tasksFile = JSON.parse(tasksContent);
            if (tasksFile.tasks && tasksFile.tasks.length > 0 && tasksFile.tasks.every((t: { passes: boolean }) => t.passes)) {
              isReviewRecovery = true;
            }
          } catch {}
        }
      }

      if (agentRunner) {
        const acpOptions: AcpConnectionOptions = {
          cwd: worktreePath,
          issueId: issue.id,
          projectId,
          workflowLogRepo,
          coderSessionRepo,
          eventBus,
          issueNumber: issue.number,
          opencodeBinPath,
        };

        agentRunner.resumePipeline(
          issue,
          projectId,
          stateManager.getIssueRepo(),
          worktreePath,
          acpOptions,
          (issueId, status) => issueService.setStatus(issueId, status),
        );

        const response: ApiResponse = {
          success: true,
          data: {
            issue,
            message: isReviewRecovery
              ? `Issue #${number} reopened at review stage, use start to continue`
              : `Issue #${number} reopened and resumed from stage ${issue.stage}`,
          }
        };
        return c.json(response);
      }

      const response: ApiResponse = {
        success: true,
        data: {
          issue,
          message: isReviewRecovery
            ? `Issue #${number} reopened at review stage, use start to continue`
            : `Issue #${number} reopened`,
        }
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

  app.post('/:number/cleanup', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
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

      if (worktreeManager && project) {
        await worktreeManager.remove(project.path, project.name, issue.number);
      }

      const response: ApiResponse = {
        success: true,
        data: { issue, message: `Issue #${number} worktree cleaned up` }
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

  app.post('/:number/skip-to-review', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
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

      const worktreePath = worktreeManager?.getPath(project.name, issue.number) || project.path;
      const change = detectOpenSpecChange(worktreePath, issue);

      if (!change) {
        const response: ApiResponse = {
          success: false,
          error: `No OpenSpec Change found for issue #${number}. Use "mo propose ${number}" first.`
        };
        return c.json(response, 400);
      }

      issueService.transitionToStage(issue.id, Stage.Review);
      issueService.setStatus(issue.id, IssueStatus.Active);

      if (agentRunner) {
        const acpOptions: AcpConnectionOptions = {
          cwd: worktreePath,
          issueId: issue.id,
          projectId,
          workflowLogRepo,
          coderSessionRepo,
          eventBus,
          issueNumber: issue.number,
          opencodeBinPath,
        };

        agentRunner.resumePipeline(
          issue,
          projectId,
          stateManager.getIssueRepo(),
          worktreePath,
          acpOptions,
          (issueId, status) => issueService.setStatus(issueId, status),
        );
      }

      const updatedIssue = issueService.getByNumber(projectId, number);

      const response: ApiResponse = {
        success: true,
        data: {
          issue: updatedIssue,
          message: `Issue #${number} resumed, skipping to review stage. Change: ${change.changePath}`
        }
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

  app.post('/:number/approve', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
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

      if (agentRunner && agentRunner.isRunning(issue.id)) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is already running. Wait for it to complete first.`
        };
        return c.json(response, 400);
      }

      if (!agentRunner) {
        const response: ApiResponse = {
          success: false,
          error: 'AgentRunnerService not configured'
        };
        return c.json(response, 500);
      }

      const issueRepo = stateManager.getIssueRepo();
      if (!issueRepo) {
        const response: ApiResponse = {
          success: false,
          error: 'IssueRepo not configured'
        };
        return c.json(response, 500);
      }

      if (!agentRunner.hasPendingGate(number)) {
        const pendingIssue = issueRepo.findPendingApprovalByIssueId(issue.id);
        if (!(pendingIssue?.approvalState?.status === 'awaiting')) {
          const response: ApiResponse = {
            success: false,
            error: `No pending gate for issue #${number}. The pipeline may have completed or not been started. Try: mo issue start ${number}`
          };
          return c.json(response, 400);
        }
      }

      const approvalStage = issue.approvalState?.stage;
      if (approvalStage && issue.approvalState) {
        issueRepo.setApprovalState(issue.id, {
          ...issue.approvalState,
          status: 'approved',
          respondedAt: new Date().toISOString(),
        });
      }

      let nextStage: Stage | undefined;
      if (approvalStage === Stage.Plan) {
        nextStage = Stage.Build;
      } else if (approvalStage === Stage.Review) {
        nextStage = Stage.Done;
      }

      let resumedIssue = issue;
      if (nextStage) {
        resumedIssue = issueRepo.updateStage(issue.id, nextStage)!;
      }

      const project = projectService.getById(projectId);
      let worktreePath = process.cwd();
      if (worktreeManager && project) {
        const existingPath = worktreeManager.getPath(project.name, issue.number);
        worktreePath = existingPath || process.cwd();
      }

      const acpOptions: AcpConnectionOptions = {
        cwd: worktreePath,
        issueId: issue.id,
        projectId,
        workflowLogRepo,
        coderSessionRepo,
        eventBus,
        issueNumber: issue.number,
        opencodeBinPath,
      };

      agentRunner.resumePipeline(
        resumedIssue,
        projectId,
        issueRepo,
        worktreePath,
        acpOptions,
        (issueId, status) => issueService.setStatus(issueId, status),
      );

      const response: ApiResponse = {
        success: true,
        data: {
          issue: resumedIssue,
          message: `Issue #${number} approved, pipeline resumed`
        }
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

  app.post('/:number/reject', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      const body = await c.req.json().catch(() => ({}));
      const message = body.message as string | undefined;

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

      if (!issue.approvalState || issue.approvalState.status !== 'awaiting') {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is not awaiting approval`
        };
        return c.json(response, 400);
      }

      if (agentRunner && agentRunner.isRunning(issue.id)) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is already running. Wait for it to complete first.`
        };
        return c.json(response, 400);
      }

      if (!agentRunner) {
        const response: ApiResponse = {
          success: false,
          error: 'AgentRunnerService not configured'
        };
        return c.json(response, 500);
      }

      const issueRepo = stateManager.getIssueRepo();
      if (!issueRepo) {
        const response: ApiResponse = {
          success: false,
          error: 'IssueRepo not configured'
        };
        return c.json(response, 500);
      }

      if (message) {
        issueService.createComment(issue.id, message);
      }

      const rejectedStage = issue.approvalState.stage;

      issueRepo.setApprovalState(issue.id, {
        stage: rejectedStage,
        status: 'rejected',
        output: issue.approvalState.output,
        requestedAt: issue.approvalState.requestedAt,
        respondedAt: new Date().toISOString(),
      });

      let resumedIssue = issue;
      if (rejectedStage === Stage.Review) {
        resumedIssue = issueRepo.updateStage(issue.id, Stage.Build)!;
      }

      const project = projectService.getById(projectId);
      let worktreePath = process.cwd();
      if (worktreeManager && project) {
        const existingPath = worktreeManager.getPath(project.name, issue.number);
        worktreePath = existingPath || process.cwd();
      }

      const acpOptions: AcpConnectionOptions = {
        cwd: worktreePath,
        issueId: issue.id,
        projectId,
        workflowLogRepo,
        coderSessionRepo,
        eventBus,
        issueNumber: issue.number,
        opencodeBinPath,
      };

      agentRunner.resumePipeline(
        resumedIssue,
        projectId,
        issueRepo,
        worktreePath,
        acpOptions,
        (issueId, status) => issueService.setStatus(issueId, status),
      );

      const response: ApiResponse = {
        success: true,
        data: {
          issue: resumedIssue,
          message: `Issue #${number} rejected, pipeline restarted from ${rejectedStage === Stage.Review ? 'build' : 'plan'}`
        }
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

  app.post('/:number/messages', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const { message } = await c.req.json();
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      if (!message || typeof message !== 'string') {
        const response: ApiResponse = {
          success: false,
          error: 'message is required and must be a string'
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

      if (!agentRunner) {
        const response: ApiResponse = {
          success: false,
          error: 'AgentRunnerService not configured'
        };
        return c.json(response, 500);
      }

      if (!agentRunner.hasPendingGate(number)) {
        const response: ApiResponse = {
          success: false,
          error: `Pipeline is not paused for issue #${number}`
        };
        return c.json(response, 409);
      }

      const project = projectService.getById(projectId);
      let worktreePath = process.cwd();
      if (worktreeManager && project) {
        const existingPath = worktreeManager.getPath(project.name, issue.number);
        worktreePath = existingPath || process.cwd();
      }

      issueService.createComment(issue.id, message);

      const acpOptions: AcpConnectionOptions = {
        cwd: worktreePath,
        issueId: issue.id,
        projectId,
        workflowLogRepo,
        coderSessionRepo,
        eventBus,
        issueNumber: issue.number,
        opencodeBinPath,
      };

      agentRunner.resumePipeline(
        issue,
        projectId,
        stateManager.getIssueRepo(),
        worktreePath,
        acpOptions,
        (issueId, status) => issueService.setStatus(issueId, status),
      );

      const response: ApiResponse = {
        success: true,
        data: {
          issue,
          message: `Message sent to issue #${number}, agent resumed`
        }
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

  app.get('/:number/diff', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
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

      if (!worktreeManager || !worktreeManager.exists(project.name, issue.number)) {
        const response: ApiResponse = {
          success: true,
          data: { files: [] }
        };
        return c.json(response);
      }

      const branchName = `mo/issue-${number}`;
      const diffOutput = await execFileAsync(
        'git',
        ['diff', `${project.baseBranch}...${branchName}`, '--stat'],
        { cwd: project.path }
      );

      const files: Array<{ file: string; additions: number; deletions: number }> = [];
      const lines = diffOutput.stdout.trim().split('\n');
      for (const line of lines) {
        const match = line.match(/^(.+?)\s*\|\s*(\d+)\s*([+-]+)$/);
        if (match) {
          const diffSymbols = match[3] || '';
          const additions = diffSymbols.split('+').length - 1;
          const deletions = diffSymbols.split('-').length - 1;
          files.push({
            file: match[1].trim(),
            additions,
            deletions,
          });
        }
      }

      const response: ApiResponse = {
        success: true,
        data: { files }
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

  app.get('/:number/logs', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>'
        };
        return c.json(response, 400);
      }

      if (!workflowLogRepo) {
        const response: ApiResponse = {
          success: false,
          error: 'WorkflowLog not configured'
        };
        return c.json(response, 500);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const eventType = c.req.query('eventType') as string | undefined;
      const logs = workflowLogRepo.findByIssueId(issue.id, eventType);
      const entries = logs.map(log => ({
        id: log.id,
        eventType: log.eventType,
        data: (() => { try { return JSON.parse(log.data); } catch { return log.data; } })(),
        createdAt: log.createdAt,
      }));

      const response: ApiResponse = {
        success: true,
        data: entries
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

  app.get('/:number/checkpoint', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
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

      if (!checkpointRepo) {
        const response: ApiResponse = {
          success: false,
          error: 'PipelineCheckpointRepo not configured'
        };
        return c.json(response, 500);
      }

      const stages: string[] = ['plan', 'build'];
      const data = stages
        .map(stage => checkpointRepo!.get(issue.number, stage))
        .filter((cp): cp is NonNullable<typeof cp> => cp !== null)
        .map(cp => ({
          stage: cp.stage,
          completedSteps: cp.completedSteps,
          nextStep: cp.nextStep,
          updatedAt: cp.updatedAt,
        }));

      const response: ApiResponse = {
        success: true,
        data
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

  app.get('/:number/agent-session', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
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

      if (!agentSessionMessageRepo) {
        const response: ApiResponse = {
          success: false,
          error: 'AgentSessionMessageRepo not configured'
        };
        return c.json(response, 500);
      }

      const messages = agentSessionMessageRepo.findByIssueId(issue.id);
      const data = messages.map(m => ({
        id: m.id,
        role: m.role,
        content: m.content,
        toolCalls: m.toolCalls,
        toolCallId: m.toolCallId,
        toolName: m.toolName,
        toolResult: m.toolResult,
        stepIndex: m.stepIndex,
        createdAt: m.createdAt,
      }));

      const response: ApiResponse = {
        success: true,
        data
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

  app.get('/:number/coder-sessions', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
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

      if (!coderSessionRepo || !workflowLogRepo) {
        const response: ApiResponse = {
          success: false,
          error: 'CoderSessionRepo or WorkflowLogRepo not configured'
        };
        return c.json(response, 500);
      }

      const sessions = coderSessionRepo.findByIssueId(issue.id);
      const data = sessions.map(session => {
        const logs = workflowLogRepo.findBySessionId(session.acpSessionId);
        return {
          id: session.id,
          acpSessionId: session.acpSessionId,
          executionId: session.executionId,
          taskDescription: session.taskDescription,
          status: session.status,
          createdAt: session.createdAt,
          completedAt: session.completedAt,
          workflowLogs: logs.map(l => ({
            id: l.id,
            eventType: l.eventType,
            data: (() => { try { return JSON.parse(l.data); } catch { return l.data; } })(),
            createdAt: l.createdAt,
          })),
        };
      });

      const response: ApiResponse = {
        success: true,
        data
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

  app.get('/:number/build-status', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
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
        return c.json(response, 400);
      }

      const worktreePath = worktreeManager?.getPath(project.name, issue.number) || project.path;
      const change = detectOpenSpecChange(worktreePath, issue);

      if (!change) {
        const response: ApiResponse = {
          success: true,
          data: {
            stage: issue.stage,
            status: issue.stage === Stage.Build ? 'running' : (issue.stage === Stage.Done ? 'completed' : 'pending'),
            progress: { completed: 0, failed: 0, total: 0, currentTask: null },
            tasks: [],
          }
        };
        return c.json(response);
      }

      let tasks: Array<{ id: string; title: string; passes: boolean; attempts: number; error?: string | null }> = [];
      let total = 0;
      let completed = 0;
      let failed = 0;
      let currentTask: string | null = null;

      try {
        const tasksContent = fs.readFileSync(change.tasksPath, 'utf-8');
        const tasksFile = JSON.parse(tasksContent) as { tasks: Array<{ id: string; title: string; passes: boolean; attempts: number; error?: string | null }> };
        tasks = tasksFile.tasks;
        total = tasks.length;
        completed = tasks.filter(t => t.passes).length;
        failed = tasks.filter(t => t.error && !t.passes).length;
        const pending = tasks.find(t => !t.passes);
        currentTask = pending ? pending.id : null;
      } catch {
        log.warn('Failed to read tasks for build-status', { tasksPath: change.tasksPath, issueNumber: number });
      }

      let status: string;
      if (issue.stage === Stage.Done) {
        status = 'completed';
      } else if (issue.stage === Stage.Build) {
        status = 'running';
      } else if (completed === total && total > 0) {
        status = 'completed';
      } else {
        status = 'pending';
      }

      const response: ApiResponse = {
        success: true,
        data: {
          stage: issue.stage === Stage.Build ? 'build' : issue.stage,
          status,
          progress: { completed, failed, total, currentTask },
          tasks,
        }
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

  app.get('/:number/tasks', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
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
        return c.json(response, 400);
      }

      const worktreePath = worktreeManager?.getPath(project.name, issue.number) || project.path;
      const change = detectOpenSpecChange(worktreePath, issue);

      if (!change) {
        const response: ApiResponse = {
          success: true,
          data: { version: 1, tasks: [] }
        };
        return c.json(response);
      }

      try {
        const tasksContent = fs.readFileSync(change.tasksPath, 'utf-8');
        const tasksFile = JSON.parse(tasksContent);
        const response: ApiResponse = {
          success: true,
          data: tasksFile
        };
        return c.json(response);
      } catch {
        const response: ApiResponse = {
          success: true,
          data: { version: 1, tasks: [] }
        };
        return c.json(response);
      }
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      return c.json(response, 500);
    }
  });

  app.post('/:number/merge', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      const project = projectService.getById(projectId);
      if (!project) {
        return c.json({ success: false, error: 'Project not found' } satisfies ApiResponse, 404);
      }

      if (!worktreeManager) {
        return c.json({ success: false, error: 'WorktreeManager not configured' } satisfies ApiResponse, 500);
      }

      if (!worktreeManager.exists(project.name, issue.number)) {
        return c.json({ success: false, error: `No worktree found for issue #${number}` } satisfies ApiResponse, 404);
      }

      const result = await worktreeManager.mergeBack(project.path, project.name, issue.number, project.baseBranch);

      if (!result.success) {
        return c.json({ success: false, error: result.message } satisfies ApiResponse, 409);
      }

      await worktreeManager.remove(project.path, project.name, issue.number).catch((err) => {
        log.warn('Failed to cleanup worktree after merge', { number, error: err instanceof Error ? err.message : String(err) });
      });

      return c.json({ success: true, data: { issue, message: result.message } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.get('/worktrees', async (c) => {
    try {
      const projectId = getCurrentProjectId();
      if (!projectId) {
        return c.json({ success: false, error: 'No active project' } satisfies ApiResponse, 400);
      }

      const project = projectService.getById(projectId);
      if (!project) {
        return c.json({ success: false, error: 'Project not found' } satisfies ApiResponse, 404);
      }

      if (!worktreeManager) {
        return c.json({ success: false, error: 'WorktreeManager not configured' } satisfies ApiResponse, 500);
      }

      const worktrees = await worktreeManager.list(project.path);

      return c.json({ success: true, data: { worktrees } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.post('/:number/retry-merge', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (!mergeQueue) {
        return c.json({ success: false, error: 'MergeQueue not configured' } satisfies ApiResponse, 500);
      }

      const project = projectService.getById(projectId);
      if (project && worktreeManager && !worktreeManager.exists(project.name, issue.number)) {
        return c.json({ success: false, error: `No worktree found for issue #${number}` } satisfies ApiResponse, 404);
      }

      const retried = mergeQueue.retry(number);
      if (!retried) {
        return c.json({ success: false, error: `Issue #${number} is not in a retryable merge state (build-failed or conflict)` } satisfies ApiResponse, 409);
      }

      return c.json({ success: true, data: { message: `Issue #${number} re-enqueued for merge` } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  return app;
}
