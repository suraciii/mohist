import { Hono } from 'hono';
import * as fs from 'fs';
import * as path from 'path';
import { StateManager } from '../server/state-manager';
import { ApiResponse, Issue, Stage, IssueStatus, Comment, Priority, VALID_PRIORITIES, MergeState } from '../types';
import { IssueService } from '../services';
import { ProjectService } from '../services';
import { AgentRunnerService } from '../services';
import { WorktreeManager, smartFetch } from '../git/worktree-manager';
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

export type ResolveConflictsFn = (issue: Issue, worktreePath: string, conflictFiles: string[]) => Promise<{ success: boolean; error?: string }>;

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
  resolveConflicts?: ResolveConflictsFn,
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
      const showArchived = c.req.query('archived') === 'true';
      const showAll = c.req.query('all') === 'true';

      if (priority && !VALID_PRIORITIES.includes(priority as Priority)) {
        const response: ApiResponse = {
          success: false,
          error: 'Invalid priority'
        };
        return c.json(response, 400);
      }

      const issueRepo = stateManager.getIssueRepo();
      let issues: Issue[];
      if (showArchived) {
        issues = issueRepo.findAll({ projectId, stage, archivedOnly: true });
      } else if (showAll) {
        issues = issueRepo.findAll({ projectId, stage, includeArchived: true });
      } else {
        issues = stage
          ? issueService.getByStage(projectId, stage)
          : issueService.getByProject(projectId);
      }

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

  app.post('/archive-completed', async (c) => {
    try {
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const result = await issueService.archiveAllCompleted(projectId);

      const response: ApiResponse = {
        success: true,
        data: { archived: result.count, message: result.message }
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

  app.get('/merge-blocked', async (c) => {
    try {
      const projectId = c.req.query('projectId') || getCurrentProjectId();
      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issueRepo = stateManager.getIssueRepo();
      if (!issueRepo) {
        return c.json({ success: false, error: 'IssueRepo not configured' } satisfies ApiResponse, 500);
      }

      const blockedIssues = issueRepo.findByMergeStates([MergeState.Blocked])
        .filter(issue => issue.projectId === projectId);

      const blockedEntries = blockedIssues.map(issue => {
        const queueEntry = mergeQueue?.getStatus().find(e => e.issueNumber === issue.number);
        return {
          issueNumber: issue.number,
          title: issue.title,
          conflictingFiles: queueEntry?.conflictingFiles ?? [],
          blockedAt: issue.updatedAt,
        };
      });

      return c.json({ success: true, data: blockedEntries } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
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

      const { title, body, addLabels, removeLabels, priority, model } = await c.req.json();

      if (priority !== undefined && !VALID_PRIORITIES.includes(priority as Priority)) {
        const response: ApiResponse = {
          success: false,
          error: 'Invalid priority'
        };
        return c.json(response, 400);
      }

      if (model !== undefined && model !== null && typeof model === 'string' && !model.includes('/')) {
        const response: ApiResponse = {
          success: false,
          error: 'Invalid model format'
        };
        return c.json(response, 400);
      }

      const updateData: Partial<{ title: string; body: string; labels: string[]; priority: Priority; model: string | null }> = {};
      
      if (title !== undefined) updateData.title = title;
      if (body !== undefined) updateData.body = body;
      if (priority !== undefined) updateData.priority = priority;
      if (model !== undefined) updateData.model = model;
      
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
          error: `Issue #${number} is blocked. Use retry to resume from checkpoint or restart to start over.`
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

      if (issue.stage !== Stage.Backlog) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is not in backlog stage (current: ${issue.stage}). Only backlog issues can be started.`
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
        model: updatedIssue.model ?? undefined,
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
            issueService.transitionToStage(issue.id, Stage.Backlog);
          }
        } catch (rollbackError) {
          log.error('Failed to rollback stage to Backlog', { error: rollbackError instanceof Error ? rollbackError.message : rollbackError });
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

  app.post('/:number/force-stop', async (c) => {
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

      const result = agentRunner.forceStop(issue.id);
      if (!result.stopped) {
        const response: ApiResponse = {
          success: false,
          error: `No agent running for issue #${number}`
        };
        return c.json(response, 409);
      }

      issueService.setStatus(issue.id, IssueStatus.Interrupted);

      const response: ApiResponse = {
        success: true,
        data: { ok: true as const, issueNumber: number }
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

  app.post('/:number/archive', async (c) => {
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
          error: `Issue #${number} has a running agent. Force-stop it first.`
        };
        return c.json(response, 409);
      }

      const { cleanup } = await c.req.json().catch(() => ({ cleanup: true }));
      const result = await issueService.archive(projectId, number, { cleanup });

      const response: ApiResponse = {
        success: true,
        data: {
          issue: result.issue,
          warning: result.warning,
          message: `Issue #${number} archived`,
        }
      };
      return c.json(response);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Unknown error';
      const response: ApiResponse = {
        success: false,
        error: message
      };
      return c.json(response, 500);
    }
  });

  app.post('/:number/unarchive', async (c) => {
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

      if (!issue.archivedAt) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is not archived`
        };
        return c.json(response, 400);
      }

      const result = await issueService.unarchive(projectId, number);

      const response: ApiResponse = {
        success: true,
        data: {
          issue: result,
          message: `Issue #${number} unarchived`,
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

      const issueRepo = stateManager.getIssueRepo();
      if (issueRepo) {
        issueRepo.updateBlockedReason(issue.id, null);
        issueRepo.updateRetryCount(issue.id, 0);
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
          model: issue.model ?? undefined,
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

  app.post('/:number/retry', async (c) => {
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

      if (issue.status !== IssueStatus.Blocked) {
        return c.json({ success: false, error: 'Issue is not blocked' } satisfies ApiResponse, 409);
      }

      if (agentRunner && agentRunner.isRunning(issue.id)) {
        return c.json({ success: false, error: 'Agent is already running for this issue' } satisfies ApiResponse, 409);
      }

      const issueRepo = stateManager.getIssueRepo();
      if (!issueRepo) {
        return c.json({ success: false, error: 'IssueRepo not configured' } satisfies ApiResponse, 500);
      }

      issueRepo.updateBlockedReason(issue.id, null);
      issueRepo.updateRetryCount(issue.id, 0);

      const project = projectService.getById(projectId);
      let worktreePath = process.cwd();
      let hasCheckpoint = false;

      if (worktreeManager && project) {
        const existingPath = worktreeManager.getPath(project.name, issue.number);
        if (existingPath) {
          worktreePath = existingPath;
          const changeDir = findChangeDir(worktreePath, issue.number);
          if (changeDir) {
            const tasksPath = path.join(changeDir, 'tasks.json');
            if (fs.existsSync(tasksPath)) {
              hasCheckpoint = true;
            }
          }
        }
      }

      if (!hasCheckpoint) {
        issueRepo.updateStage(issue.id, Stage.Backlog);
        issueRepo.updateStatus(issue.id, IssueStatus.Active);
        issueRepo.clearApprovalState(issue.id);

        const response: ApiResponse = {
          success: true,
          data: {
            issue: issueRepo.findById(issue.id),
            message: 'no checkpoint found, reset to draft',
          }
        };
        return c.json(response);
      }

      issueRepo.updateStatus(issue.id, IssueStatus.Active);

      const updatedIssue = issueRepo.findById(issue.id);
      if (!updatedIssue) {
        return c.json({ success: false, error: 'Failed to refresh issue after retry' } satisfies ApiResponse, 500);
      }

      if (agentRunner) {
        const acpOptions: AcpConnectionOptions = {
          cwd: worktreePath,
          issueId: updatedIssue.id,
          projectId,
          workflowLogRepo,
          coderSessionRepo,
          eventBus,
          issueNumber: updatedIssue.number,
          opencodeBinPath,
          model: updatedIssue.model ?? undefined,
          stage: updatedIssue.stage,
        };

        agentRunner.resumePipeline(
          updatedIssue,
          projectId,
          issueRepo,
          worktreePath,
          acpOptions,
          (issueId, status) => issueService.setStatus(issueId, status),
        );
      }

      const response: ApiResponse = {
        success: true,
        data: {
          issue: updatedIssue,
          message: 'retrying from checkpoint',
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

  app.post('/:number/restart', async (c) => {
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

      if (issue.status !== IssueStatus.Blocked) {
        return c.json({ success: false, error: 'Issue is not blocked' } satisfies ApiResponse, 409);
      }

      if (agentRunner && agentRunner.isRunning(issue.id)) {
        return c.json({ success: false, error: 'Agent is already running for this issue' } satisfies ApiResponse, 409);
      }

      const issueRepo = stateManager.getIssueRepo();
      if (!issueRepo) {
        return c.json({ success: false, error: 'IssueRepo not configured' } satisfies ApiResponse, 500);
      }

      issueRepo.updateStage(issue.id, Stage.Backlog);
      issueRepo.updateBlockedReason(issue.id, null);
      issueRepo.updateRetryCount(issue.id, 0);
      issueRepo.clearApprovalState(issue.id);
      issueRepo.updateStatus(issue.id, IssueStatus.Active);

      const updatedIssue = issueRepo.findById(issue.id);

      const response: ApiResponse = {
        success: true,
        data: {
          issue: updatedIssue,
          message: 'reset to draft, use start to begin again',
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
          model: issue.model ?? undefined,
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
        model: issue.model ?? undefined,
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
        model: issue.model ?? undefined,
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
        model: issue.model ?? undefined,
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
          data: { files: [], totalAdditions: 0, totalDeletions: 0 }
        };
        return c.json(response);
      }

      const branchName = `mo/issue-${number}`;
      const diffRef = `${project.baseBranch}...${branchName}`;

      const numstatOutput = await execFileAsync(
        'git',
        ['diff', diffRef, '--numstat'],
        { cwd: project.path }
      );

      const fileStats = new Map<string, { additions: number; deletions: number; isBinary: boolean }>();
      const numstatLines = numstatOutput.stdout.trim().split('\n').filter(l => l.trim());
      for (const line of numstatLines) {
        const parts = line.split('\t');
        if (parts.length >= 3) {
          const [addStr, delStr, filePath] = parts;
          const isBinary = addStr === '-' && delStr === '-';
          fileStats.set(filePath, {
            additions: isBinary ? 0 : parseInt(addStr, 10) || 0,
            deletions: isBinary ? 0 : parseInt(delStr, 10) || 0,
            isBinary,
          });
        }
      }

      const patchOutput = await execFileAsync(
        'git',
        ['diff', diffRef],
        { cwd: project.path }
      );

      const perFileDiffs = new Map<string, string>();
      const fullPatch = patchOutput.stdout;
      if (fullPatch.trim()) {
        const fileBlocks = fullPatch.split(/(?=^diff --git )/m);
        for (const block of fileBlocks) {
          if (!block.trim()) continue;
          const headerMatch = block.match(/^diff --git (?:a\/.*?|\/dev\/null) (?:b\/(.*?)|\/dev\/null)/);
          if (headerMatch) {
            const filePath = headerMatch[1];
            perFileDiffs.set(filePath, block);
          }
        }
      }

      const files: Array<{ file: string; additions: number; deletions: number; diff: string }> = [];
      let totalAdditions = 0;
      let totalDeletions = 0;

      for (const [filePath, stats] of fileStats) {
        totalAdditions += stats.additions;
        totalDeletions += stats.deletions;
        files.push({
          file: filePath,
          additions: stats.additions,
          deletions: stats.deletions,
          diff: stats.isBinary
            ? 'Binary file, no diff available'
            : (perFileDiffs.get(filePath) || ''),
        });
      }

      const response: ApiResponse = {
        success: true,
        data: { files, totalAdditions, totalDeletions }
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

  app.get('/:number/commits', async (c) => {
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
          data: { commits: [] }
        };
        return c.json(response);
      }

      const branchName = `mo/issue-${number}`;
      const logOutput = await execFileAsync(
        'git',
        ['log', `${project.baseBranch}..${branchName}`, '--format=----COMMIT----%h%x00%s%x00%an%x00%aI', '--stat'],
        { cwd: project.path }
      );

      const commits: Array<{ hash: string; message: string; author: string; date: string; filesChanged: number; additions: number; deletions: number }> = [];
      const rawOutput = logOutput.stdout.trim();

      if (rawOutput) {
        const entries = rawOutput.split('----COMMIT----').filter(e => e.trim());
        for (const entry of entries) {
          const [headerLine, ...statLines] = entry.trim().split('\n');
          const parts = headerLine.split('\x00');
          if (parts.length < 4) continue;

          const hash = parts[0].trim();
          const message = parts[1].trim();
          const author = parts[2].trim();
          const date = parts[3].trim();

          let filesChanged = 0;
          let additions = 0;
          let deletions = 0;

          const statSummary = statLines.find(l => l.includes('files changed') || l.includes('file changed'));
          if (statSummary) {
            const fc = statSummary.match(/(\d+) files? changed/);
            if (fc) filesChanged = parseInt(fc[1], 10);
            const ins = statSummary.match(/(\d+) insertions?\(\+\)/);
            if (ins) additions = parseInt(ins[1], 10);
            const del = statSummary.match(/(\d+) deletions?\(-\)/);
            if (del) deletions = parseInt(del[1], 10);
          }

          commits.push({ hash, message, author, date, filesChanged, additions, deletions });
        }
      }

      const response: ApiResponse = {
        success: true,
        data: { commits }
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

  app.get('/:number/commits/:hash/diff', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const hash = c.req.param('hash');
      const projectId = getCurrentProjectId();

      if (!/^[0-9a-f]{7,40}$/i.test(hash)) {
        const response: ApiResponse = {
          success: false,
          error: 'Invalid commit hash'
        };
        return c.json(response, 400);
      }

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
          success: false,
          error: `No worktree for issue #${number}`
        };
        return c.json(response, 404);
      }

      const branchName = `mo/issue-${number}`;
      const containsOutput = await execFileAsync(
        'git',
        ['branch', '--contains', hash, '--list', branchName],
        { cwd: project.path }
      );

      if (!containsOutput.stdout.trim()) {
        const response: ApiResponse = {
          success: false,
          error: `Commit ${hash} does not belong to branch ${branchName}`
        };
        return c.json(response, 404);
      }

      const diffOutput = await execFileAsync(
        'git',
        ['show', '--format=', '--patch', hash],
        { cwd: project.path }
      );

      const response: ApiResponse = {
        success: true,
        data: { hash, diff: diffOutput.stdout }
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
          model: session.model,
          coderType: session.coderType,
          stage: session.stage,
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

      let tasks: Array<{ id: string; title: string; passes: boolean; attempts: number; error?: string | null; durations?: number[] }> = [];
      let total = 0;
      let completed = 0;
      let failed = 0;
      let currentTask: string | null = null;

      try {
        const tasksContent = fs.readFileSync(change.tasksPath, 'utf-8');
        const tasksFile = JSON.parse(tasksContent) as { tasks: Array<{ id: string; title: string; passes: boolean; attempts: number; error?: string | null; durations?: number[] }> };
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

  const REBASE_ALLOWED_STAGES: Stage[] = [Stage.Plan, Stage.Build, Stage.Review, Stage.Done];

  async function handleReviewRebase(issue: Issue, project: { name: string; baseBranch: string }, projectId: string, number: number): Promise<boolean | undefined> {
    eventBus.emit('rebase_progress', { issueId: issue.id, projectId, issueNumber: number, step: 'verifying' });
    try {
      const worktreePath = worktreeManager!.getPath(project.name, issue.number);
      if (worktreePath) {
        await execFileAsync('npm', ['run', 'build'], {
          cwd: worktreePath,
          timeout: 5 * 60 * 1000,
          maxBuffer: 10 * 1024 * 1024,
        });
        return true;
      }
    } catch {}
    return false;
  }

  function handlePlanRebase(issue: Issue, project: { name: string }, projectId: string, number: number): void {
    if (!agentRunner) return;
    if (!agentRunner.hasPendingGate(number)) {
      log.info('Skipping re-self-review injection: no pending gate for issue', { issueNumber: number });
      return;
    }
    const rebaseMessage = 'master has new changes after rebase. Please re-evaluate design artifacts: check if design/tasks can leverage the new code, and verify all file paths referenced in tasks.json still exist in the updated codebase.';
    const worktreePath = worktreeManager!.getPath(project.name, issue.number) || process.cwd();
    issueService.createComment(issue.id, rebaseMessage);
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

  function handleBuildRebase(issue: Issue, project: { name: string }, number: number): void {
    if (!checkpointRepo) return;
    const changeDir = findChangeDir(
      worktreeManager!.getPath(project.name, issue.number) || process.cwd(),
      issue.number,
    );
    if (!changeDir) return;
    try {
      const tasksPath = path.join(changeDir, 'tasks.json');
      const tasksContent = fs.readFileSync(tasksPath, 'utf-8');
      const tasksFile = JSON.parse(tasksContent);
      for (const task of tasksFile.tasks) {
        task.passes = false;
        task.error = null;
        task.attempts = 0;
      }
      fs.writeFileSync(tasksPath, JSON.stringify(tasksFile, null, 2), 'utf-8');
      checkpointRepo.delete(issue.number, 'build');
    } catch (err) {
      log.warn('Failed to clear build checkpoint after rebase', { issueNumber: number, error: err instanceof Error ? err.message : String(err) });
    }
  }

  app.post('/:number/rebase', async (c) => {
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

      if (!REBASE_ALLOWED_STAGES.includes(issue.stage)) {
        return c.json({ success: false, error: `Rebase not available in current stage (${issue.stage})` } satisfies ApiResponse, 400);
      }

      if (issue.stage === Stage.Done) {
        if (!mergeQueue) {
          return c.json({ success: false, error: 'MergeQueue not configured' } satisfies ApiResponse, 500);
        }
        const retried = mergeQueue.retry(number);
        if (!retried) {
          return c.json({ success: false, error: `Issue #${number} is not in a retryable merge state` } satisfies ApiResponse, 409);
        }
        return c.json({ success: true, data: { rebased: true, message: 'Rebase delegated to merge queue retry' } } satisfies ApiResponse);
      }

      const project = projectService.getById(projectId);
      if (!project) {
        return c.json({ success: false, error: 'Project not found' } satisfies ApiResponse, 404);
      }

      if (!worktreeManager) {
        return c.json({ success: false, error: 'WorktreeManager not configured' } satisfies ApiResponse, 500);
      }

      if (!worktreeManager.exists(project.name, issue.number)) {
        return c.json({ success: false, error: 'Worktree not found' } satisfies ApiResponse, 400);
      }

      if (agentRunner && agentRunner.isRunning(issue.id)) {
        return c.json({ success: false, error: 'Agent is running' } satisfies ApiResponse, 409);
      }

      eventBus.emit('rebase_started', { issueId: issue.id, projectId, issueNumber: number });

      eventBus.emit('rebase_progress', { issueId: issue.id, projectId, issueNumber: number, step: 'fetching' });
      await smartFetch(project.path);

      eventBus.emit('rebase_progress', { issueId: issue.id, projectId, issueNumber: number, step: 'checking' });
      const canFF = await worktreeManager.canFastForward(project.path, project.name, issue.number, project.baseBranch);

      if (canFF) {
        eventBus.emit('rebase_completed', { issueId: issue.id, projectId, issueNumber: number, rebased: false });
        return c.json({ success: true, data: { rebased: false, message: 'Already up to date' } } satisfies ApiResponse);
      }

      eventBus.emit('rebase_progress', { issueId: issue.id, projectId, issueNumber: number, step: 'rebasing' });
      const rebaseResult = await worktreeManager.rebaseOntoMaster(
        project.path,
        project.name,
        issue.number,
        project.baseBranch,
        { abortOnConflict: false },
      );

      if (!rebaseResult.success) {
        const conflicts = rebaseResult.conflicts;

        if (resolveConflicts && conflicts.length > 0) {
          eventBus.emit('rebase_conflict', {
            issueId: issue.id,
            projectId,
            issueNumber: number,
            conflicts,
            status: 'resolving',
          });

          try {
            const worktreePath = worktreeManager.getPath(project.name, issue.number);
            const resolved = await resolveConflicts(issue, worktreePath || '', conflicts);

            if (!resolved.success) {
              await worktreeManager.abortRebase(project.name, issue.number);
              eventBus.emit('rebase_conflict', {
                issueId: issue.id,
                projectId,
                issueNumber: number,
                conflicts,
                status: 'failed',
              });
              return c.json({
                success: false,
                error: resolved.error || 'Auto-resolve failed',
                data: { rebased: false, conflicts, message: `Auto-resolve failed: ${resolved.error || 'unknown error'}` },
              } satisfies ApiResponse, 409);
            }

            const continueResult = await worktreeManager.rebaseContinue(project.name, issue.number);
            if (!continueResult.success) {
              await worktreeManager.abortRebase(project.name, issue.number);
              eventBus.emit('rebase_conflict', {
                issueId: issue.id,
                projectId,
                issueNumber: number,
                conflicts: continueResult.conflicts,
                status: 'failed',
              });
              return c.json({
                success: false,
                error: 'Rebase conflicts remain after auto-resolve',
                data: { rebased: false, conflicts: continueResult.conflicts, message: 'Rebase conflicts remain after auto-resolve' },
              } satisfies ApiResponse, 409);
            }

            let buildPassed: boolean | undefined;
            if (issue.stage === Stage.Review) {
              buildPassed = await handleReviewRebase(issue, project, projectId, number);
            }

            eventBus.emit('rebase_completed', { issueId: issue.id, projectId, issueNumber: number, rebased: true });

            if (issue.stage === Stage.Plan) {
              handlePlanRebase(issue, project, projectId, number);
            }

            if (issue.stage === Stage.Build) {
              handleBuildRebase(issue, project, number);
            }

            const response: ApiResponse = {
              success: true,
              data: {
                rebased: true,
                autoResolved: true,
                message: issue.stage === Stage.Review
                  ? (buildPassed ? 'Rebase successful with auto-resolved conflicts, build verification passed' : 'Rebase successful with auto-resolved conflicts but build verification failed')
                  : issue.stage === Stage.Build
                    ? 'Rebase successful with auto-resolved conflicts, checkpoint cleared, resume pipeline to rebuild'
                    : 'Rebase successful with auto-resolved conflicts',
                ...(buildPassed !== undefined && { buildPassed }),
              },
            };
            return c.json(response);
          } catch (resolveErr) {
            await worktreeManager.abortRebase(project.name, issue.number).catch(() => {});
            eventBus.emit('rebase_conflict', {
              issueId: issue.id,
              projectId,
              issueNumber: number,
              conflicts,
              status: 'failed',
            });
            return c.json({
              success: false,
              error: resolveErr instanceof Error ? resolveErr.message : 'Auto-resolve threw',
              data: { rebased: false, conflicts, message: `Auto-resolve error: ${resolveErr instanceof Error ? resolveErr.message : 'unknown'}` },
            } satisfies ApiResponse, 409);
          }
        }

        await worktreeManager.abortRebase(project.name, issue.number);
        eventBus.emit('rebase_conflict', {
          issueId: issue.id,
          projectId,
          issueNumber: number,
          conflicts,
        });
        return c.json({
          success: false,
          error: 'Rebase aborted due to conflicts',
          data: { rebased: false, conflicts, message: 'Rebase aborted due to conflicts' },
        } satisfies ApiResponse, 409);
      }

      let buildPassed: boolean | undefined;
      if (issue.stage === Stage.Review) {
        buildPassed = await handleReviewRebase(issue, project, projectId, number);
      }

      eventBus.emit('rebase_completed', { issueId: issue.id, projectId, issueNumber: number, rebased: true });

      if (issue.stage === Stage.Plan) {
        handlePlanRebase(issue, project, projectId, number);
      }

      if (issue.stage === Stage.Build) {
        handleBuildRebase(issue, project, number);
      }

      const response: ApiResponse = {
        success: true,
        data: {
          rebased: true,
          message: issue.stage === Stage.Review
            ? (buildPassed ? 'Rebase successful, build verification passed' : 'Rebase successful but build verification failed')
            : issue.stage === Stage.Build
              ? 'Rebase successful, checkpoint cleared, resume pipeline to rebuild'
              : 'Rebase successful',
          ...(buildPassed !== undefined && { buildPassed }),
        },
      };
      return c.json(response);
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
        const queueEntry = mergeQueue.getStatus().find(e => e.issueNumber === number);
        const currentState = queueEntry?.mergeState ?? 'unknown';
        return c.json({ success: false, error: `Issue #${number} is not in a retryable merge state (current state: ${currentState}; retryable: build-failed, conflict, blocked)` } satisfies ApiResponse, 409);
      }

      return c.json({ success: true, data: { message: `Issue #${number} re-enqueued for merge` } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  return app;
}
