import { Hono } from 'hono';
import * as fs from 'fs';
import * as path from 'path';
import { StateManager } from '../server/state-manager';
import { ApiResponse, Issue, Stage, IssueStatus, Comment, Priority, VALID_PRIORITIES, MergeState } from '../types';
import { IssueService } from '../services';
import { ProjectService } from '../services';
import { AgentRunnerService } from '../services';
import type { ConflictResolutionDeps } from '../services/conflict-resolution';
import { WorktreeManager } from '../git/worktree-manager';
import { MergeQueue } from '../git/merge-queue';
import type { LlmConfig } from '../agent-runtime';
import { WorkflowLogRepo } from '../db/workflow-log-repo';
import { SessionStreamLogRepo } from '../db/session-stream-log-repo';
import { AgentSessionMessageRepo } from '../db/agent-session-message-repo';
import { CoderSessionRepo } from '../db/coder-session-repo';
import { PipelineCheckpointRepo } from '../db/pipeline-checkpoint-repo';
import { CheckSuiteRepo } from '../db/check-suite-repo';
import { detectOpenSpecChange, findChangeDir } from '../openspec/detector';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { Log } from '../util/log';
import type { IssueQueueStatus } from '../services/agent-runner-service';

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
  sessionStreamLogRepo?: SessionStreamLogRepo,
  agentSessionMessageRepo?: AgentSessionMessageRepo,
  coderSessionRepo?: CoderSessionRepo,
  _opencodeBinPath?: string,
  mergeQueue?: MergeQueue,
  checkpointRepo?: PipelineCheckpointRepo,
  _resolveConflictsDeps?: ConflictResolutionDeps,
  checkSuiteRepo?: CheckSuiteRepo,
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
      const archived = c.req.query('archived') as string | undefined;
      const all = c.req.query('all') as string | undefined;

      if (priority && !VALID_PRIORITIES.includes(priority as Priority)) {
        const response: ApiResponse = {
          success: false,
          error: 'Invalid priority'
        };
        return c.json(response, 400);
      }

      const issueRepo = stateManager.getIssueRepo();
      let issues: Issue[];
      if (archived === 'true') {
        issues = issueRepo.findAll({ projectId, archivedOnly: true });
      } else if (all === 'true') {
        issues = issueRepo.findAll({ projectId, includeArchived: true });
      } else if (stage) {
        issues = issueService.getByStage(projectId, stage).filter(issue => !issue.archivedAt);
      } else {
        issues = issueService.getByProject(projectId);
      }

      if (all !== 'true' && archived !== 'true') {
        issues = issues.filter(issue => !issue.archivedAt);
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
      return c.json({ success: true, data: { archived: result.count, message: result.message } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.post('/:number/archive', async (c) => {
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

      const { cleanup } = await c.req.json().catch(() => ({ cleanup: true }));
      const result = await issueService.archive(projectId, number, { cleanup: cleanup !== false });
      return c.json({ success: true, data: { issue: result.issue, warning: result.warning, message: `Issue #${number} archived` } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.post('/:number/unarchive', async (c) => {
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

      if (!issue.archivedAt) {
        return c.json({ success: false, error: `Issue #${number} is not archived` } satisfies ApiResponse, 400);
      }

      const result = await issueService.unarchive(projectId, number);
      return c.json({ success: true, data: { issue: result, message: `Issue #${number} unarchived` } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
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

  app.get('/:number/check-suite', async (c) => {
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

      if (!checkSuiteRepo) {
        return c.json({ success: false, error: 'CheckSuiteRepo not configured' } satisfies ApiResponse, 500);
      }

      const checkSuite = checkSuiteRepo.findActiveByIssueId(issue.id);
      return c.json({ success: true, data: checkSuite } satisfies ApiResponse);
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

      const checkSuite = checkSuiteRepo ? checkSuiteRepo.findActiveByIssueId(issue.id) : null;

      const response: ApiResponse = {
        success: true,
        data: {
          ...issue,
          projectName: project?.name || 'unknown',
          projectPath: project?.path || '',
          baseBranch: project?.baseBranch || 'main',
          comments,
          checkSuite
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
          error: `Issue #${number} is blocked. Run: mo issue retry ${number} or mo issue restart ${number}`
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

      if (issue.stage !== Stage.Draft && issue.stage !== Stage.Backlog) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is not in a startable stage (current: ${issue.stage}). Only draft/backlog issues can be started.`
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

      const result = agentRunner.enqueue(issue.id, 'start-pipeline');

      const response: ApiResponse = {
        success: true,
        data: {
          taskId: result.taskId,
          status: result.status,
          queuePosition: result.queuePosition,
          message: `Issue #${number} enqueued for start-pipeline`,
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

      agentRunner.cancelAll(issue.id);
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

      if (agentRunner) {
        const queueStatus = agentRunner.getQueueStatus(issue.id) as IssueQueueStatus;
        if (queueStatus.running) {
          const response: ApiResponse = {
            success: false,
            error: `Issue #${number} has a task running. Wait for it to complete or force-stop first.`
          };
          return c.json(response, 409);
        }
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

      if (agentRunner) {
        agentRunner.recoverSingleIssueById(issue.id);
      }

      const refreshedIssue = issueService.getByNumber(projectId, number);
      const isAwaitingApproval = refreshedIssue?.approvalState?.status === 'awaiting';

      if (agentRunner && !isAwaitingApproval) {
        const result = agentRunner.enqueue(issue.id, 'resume-pipeline');
        const response: ApiResponse = {
          success: true,
          data: {
            issue: refreshedIssue,
            taskId: result.taskId,
            status: result.status,
            queuePosition: result.queuePosition,
            message: `Issue #${number} reopened and enqueued for resume-pipeline`,
          }
        };
        return c.json(response, 202);
      }

      const response: ApiResponse = {
        success: true,
        data: {
          issue: refreshedIssue ?? issue,
          message: `Issue #${number} reopened at stage ${issue.stage}. Awaiting approval.`,
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

      issueService.transitionToStage(issue.id, Stage.Check);
      issueService.setStatus(issue.id, IssueStatus.Active);

      if (agentRunner) {
        const result = agentRunner.enqueue(issue.id, 'resume-pipeline');

        const updatedIssue = issueService.getByNumber(projectId, number);
        const response: ApiResponse = {
          success: true,
          data: {
            issue: updatedIssue,
            taskId: result.taskId,
            status: result.status,
            queuePosition: result.queuePosition,
            message: `Issue #${number} skipping to review stage. Change: ${change.changePath}`
          }
        };
        return c.json(response, 202);
      }

      const updatedIssue2 = issueService.getByNumber(projectId, number);

      const response2: ApiResponse = {
        success: true,
        data: {
          issue: updatedIssue2,
          message: `Issue #${number} stage set to check (no agent runner). Change: ${change.changePath}`
        }
      };
      return c.json(response2);
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

      if (!agentRunner.isIssueAwaitingApproval(issue.id)) {
        const pendingIssue = issueRepo.findPendingApprovalByIssueId(issue.id);
        if (!(pendingIssue?.approvalState?.status === 'awaiting')) {
          const response: ApiResponse = {
            success: false,
            error: `No pending approval for issue #${number}. The pipeline may have completed or not been started. Try: mo issue start ${number}`
          };
          return c.json(response, 400);
        }
      }

      if (issue.status === IssueStatus.Blocked) {
        issueRepo.updateStatus(issue.id, IssueStatus.Active);
        issueRepo.updateBlockedReason(issue.id, null);
      }

      const approvalStage = issue.approvalState?.stage;

      if (approvalStage === Stage.Check) {
        if (!mergeQueue) {
          const response: ApiResponse = {
            success: false,
            error: 'MergeQueue not configured'
          };
          return c.json(response, 500);
        }

        const project = projectService.getById(projectId);

        if (checkSuiteRepo && project) {
          const activeSuite = checkSuiteRepo.findActiveByIssueId(issue.id);
          if (activeSuite) {
            let headSha: string | null = null;
            try {
              if (worktreeManager) {
                const wtPath = worktreeManager.getPath(project.name, issue.number);
                if (wtPath) {
                  const { stdout } = await execFileAsync('git', ['rev-parse', 'HEAD'], { cwd: wtPath });
                  headSha = stdout.trim();
                }
              }
            } catch (err) {
              log.warn('Failed to read HEAD SHA for approve validation', { issueNumber: number, error: err instanceof Error ? err.message : err });
            }

            if (headSha && headSha !== activeSuite.snapshotSha) {
              checkSuiteRepo.updateStatus(activeSuite.id, 'running');
              checkSuiteRepo.updateSnapshotSha(activeSuite.id, headSha);

              if (agentRunner) {
                agentRunner.enqueue(issue.id, 'resume-pipeline');
              }

              const response: ApiResponse = {
                success: true,
                data: {
                  issue: issueService.getByNumber(projectId, number),
                  message: 'Code has changed since last check, re-running checks'
                }
              };
              return c.json(response, 202);
            }
          }
        }

        if (issue.approvalState) {
          issueRepo.setApprovalState(issue.id, {
            ...issue.approvalState,
            status: 'approved',
            respondedAt: new Date().toISOString(),
          });
        }

        mergeQueue.enqueue(projectId, number);

        const response: ApiResponse = {
          success: true,
          data: {
            issue: issueService.getByNumber(projectId, number),
            message: `Issue #${number} approved, enqueued for merge`,
          }
        };
        return c.json(response, 202);
      }

      // Plan stage: just set approval state and resume pipeline; runner will auto-advance
      if (approvalStage === Stage.Plan) {
        if (issue.approvalState) {
          issueRepo.setApprovalState(issue.id, {
            ...issue.approvalState,
            status: 'approved',
            respondedAt: new Date().toISOString(),
          });
        }
      }

      const result = agentRunner.enqueue(issue.id, 'resume-pipeline');

      const response: ApiResponse = {
        success: true,
        data: {
          issue: issueService.getByNumber(projectId, number),
          taskId: result.taskId,
          status: result.status,
          queuePosition: result.queuePosition,
          message: `Issue #${number} approved, enqueued for resume-pipeline`,
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

      if (!agentRunner) {
        const response: ApiResponse = {
          success: false,
          error: 'AgentRunnerService not configured'
        };
        return c.json(response, 500);
      }

      const rejectQueueStatus = agentRunner.getQueueStatus(issue.id) as IssueQueueStatus;
      if (rejectQueueStatus.running) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} has a running task. Wait for it to complete first.`
        };
        return c.json(response, 400);
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

      if (rejectedStage === Stage.Check) {
        issueRepo.updateStage(issue.id, Stage.Build);

        if (checkSuiteRepo) {
          const activeSuite = checkSuiteRepo.findActiveByIssueId(issue.id);
          if (activeSuite) {
            checkSuiteRepo.updateChecks(activeSuite.id, 'user-approval', {
              status: 'failed',
              output: { rejected: true, message: message || 'User rejected' },
              ranAt: new Date().toISOString(),
            });
            checkSuiteRepo.updateStatus(activeSuite.id, 'failed');
          }
        }
      }

      if (rejectedStage === Stage.Plan) {
        if (checkpointRepo) {
          checkpointRepo.delete(issue.number, 'plan');
        }
      }

      const result = agentRunner.enqueue(issue.id, 'resume-pipeline');

      const response: ApiResponse = {
        success: true,
        data: {
          issue: issueService.getByNumber(projectId, number),
          taskId: result.taskId,
          status: result.status,
          queuePosition: result.queuePosition,
          message: `Issue #${number} rejected, pipeline restarted from ${rejectedStage === Stage.Check ? 'build' : 'plan'}`
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

      if (!agentRunner.isIssueAwaitingApproval(issue.id)) {
        const response: ApiResponse = {
          success: false,
          error: `Pipeline is not paused for issue #${number}`
        };
        return c.json(response, 409);
      }

      issueService.createComment(issue.id, message);

      const result = agentRunner.enqueue(issue.id, 'resume-pipeline');

      const response: ApiResponse = {
        success: true,
        data: {
          issue: issueService.getByNumber(projectId, number),
          taskId: result.taskId,
          status: result.status,
          queuePosition: result.queuePosition,
          message: `Message sent to issue #${number}, agent resumed`
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
      const diffArgs = ['diff', `${project.baseBranch}...${branchName}`];

      const [numstatOutput, fullDiffOutput] = await Promise.all([
        execFileAsync('git', [...diffArgs, '--numstat'], { cwd: project.path }),
        execFileAsync('git', diffArgs, { cwd: project.path }),
      ]);

      const numstatEntries = new Map<string, { additions: number; deletions: number; isBinary: boolean }>();
      for (const line of numstatOutput.stdout.trim().split('\n')) {
        if (!line.trim()) continue;
        const parts = line.split('\t');
        if (parts.length < 3) continue;
        const [addStr, delStr, filePath] = parts;
        const isBinary = addStr === '-' && delStr === '-';
        numstatEntries.set(filePath, {
          additions: isBinary ? 0 : parseInt(addStr, 10),
          deletions: isBinary ? 0 : parseInt(delStr, 10),
          isBinary,
        });
      }

      const diffByFile = new Map<string, string>();
      const fullDiff = fullDiffOutput.stdout;
      if (fullDiff.trim()) {
        const blocks = fullDiff.split(/(?=^diff --git )/m);
        for (const block of blocks) {
          if (!block.trim()) continue;
          const firstLine = block.split('\n')[0];
          const match = firstLine.match(/^diff --git a\/(.+?) b\/(.+)$/);
          if (match) {
            diffByFile.set(match[2], block);
          }
        }
      }

      const files: Array<{ file: string; additions: number; deletions: number; diff: string; isBinary: boolean }> = [];
      for (const [filePath, stats] of numstatEntries) {
        files.push({
          file: filePath,
          additions: stats.additions,
          deletions: stats.deletions,
          diff: stats.isBinary ? '' : (diffByFile.get(filePath) || ''),
          isBinary: stats.isBinary,
        });
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

  app.get('/:number/worktree-status', async (c) => {
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

      if (!worktreeManager) {
        const response: ApiResponse = {
          success: true,
          data: { exists: false }
        };
        return c.json(response);
      }

      const status = await worktreeManager.getWorktreeStatus(
        project.path,
        project.name,
        issue.number,
        project.baseBranch
      );

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
        ['log', `${project.baseBranch}..${branchName}`, '--format=%h%x00%s%x00%an%x00%aI%x01', '--stat'],
        { cwd: project.path }
      );

      const commits: Array<{ hash: string; message: string; author: string; date: string; filesChanged: number; additions: number; deletions: number; files: string[] }> = [];
      const rawOutput = logOutput.stdout.trim();

      if (rawOutput) {
        const entries = rawOutput.split('\x01').filter(e => e.trim());
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

          const files: string[] = statLines
            .filter(l => !l.includes('files changed') && !l.includes('file changed'))
            .map(l => {
              const m = l.match(/^\s+(.+?)\s*\|/);
              return m ? m[1].trim() : null;
            })
            .filter((f): f is string => f !== null);

          commits.push({ hash, message, author, date, filesChanged, additions, deletions, files });
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

      const SESSION_STREAM_EVENT_TYPES = new Set([
        'agent_thought_chunk',
        'agent_message_chunk',
        'tool_call',
        'tool_call_update',
        'user_message_chunk',
      ]);

      const sessions = coderSessionRepo.findByIssueId(issue.id);
      const data = sessions.map(session => {
        let logs;
        if (sessionStreamLogRepo) {
          const streamLogs = sessionStreamLogRepo.findBySessionId(session.acpSessionId);
          if (streamLogs.length > 0) {
            logs = streamLogs;
          }
        }
        if (!logs) {
          const fallbackLogs = workflowLogRepo.findBySessionId(session.acpSessionId);
          logs = fallbackLogs.filter(l => SESSION_STREAM_EVENT_TYPES.has(l.eventType));
        }
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

  app.get('/:number/queue', async (c) => {
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

      if (!agentRunner) {
        return c.json({ success: false, error: 'AgentRunnerService not configured' } satisfies ApiResponse, 500);
      }

      const queueStatus = agentRunner.getQueueStatus(issue.id) as IssueQueueStatus;

      return c.json({
        success: true,
        data: {
          running: queueStatus.running ? {
            taskId: queueStatus.running.id,
            taskType: queueStatus.running.taskType,
            priority: queueStatus.running.priority,
            status: queueStatus.running.status,
            enqueuedAt: queueStatus.running.enqueuedAt,
            startedAt: queueStatus.running.startedAt,
          } : null,
          pending: queueStatus.pending.map(t => ({
            taskId: t.id,
            taskType: t.taskType,
            priority: t.priority,
            status: t.status,
            enqueuedAt: t.enqueuedAt,
            startedAt: t.startedAt,
          })),
          queueLength: queueStatus.queueLength,
        },
      } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.delete('/:number/queue/:taskId', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const taskId = c.req.param('taskId');
      const projectId = getCurrentProjectId();

      if (!projectId) {
        return c.json({ success: false, error: 'No active project. Use: mo project use <name>' } satisfies ApiResponse, 400);
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        return c.json({ success: false, error: `Issue #${number} not found` } satisfies ApiResponse, 404);
      }

      if (!agentRunner) {
        return c.json({ success: false, error: 'AgentRunnerService not configured' } satisfies ApiResponse, 500);
      }

      const queueStatus = agentRunner.getQueueStatus(issue.id) as IssueQueueStatus;

      const isRunningTask = queueStatus.running?.id === taskId;
      if (isRunningTask) {
        return c.json({ success: false, error: 'Cannot cancel a running task' } satisfies ApiResponse, 409);
      }

      const isPendingTask = queueStatus.pending.some(t => t.id === taskId);
      if (!isPendingTask) {
        return c.json({ success: false, error: `Task ${taskId} not found` } satisfies ApiResponse, 404);
      }

      const cancelled = agentRunner.cancel(taskId);
      if (!cancelled) {
        return c.json({ success: false, error: 'Failed to cancel task' } satisfies ApiResponse, 409);
      }

      return c.json({ success: true, data: { cancelled: true } } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  const REBASE_ALLOWED_STAGES: Stage[] = [Stage.Plan, Stage.Build, Stage.Check, Stage.Done];

  app.post('/:number/rebase', async (c) => {
    try {
      const number = parseInt(c.req.param('number'));
      const projectId = getCurrentProjectId();
      const body = await c.req.json().catch(() => ({} as Record<string, unknown>));

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

      if (!agentRunner) {
        return c.json({ success: false, error: 'AgentRunnerService not configured' } satisfies ApiResponse, 500);
      }

      const result = agentRunner.enqueue(issue.id, 'rebase', body);

      const response: ApiResponse = {
        success: true,
        data: {
          taskId: result.taskId,
          status: result.status,
          queuePosition: result.queuePosition,
          message: `Issue #${number} enqueued for rebase`,
        }
      };
      return c.json(response, 202);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
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
        return c.json({ success: false, error: `Issue #${number} is not blocked (current: ${issue.status})` } satisfies ApiResponse, 409);
      }

      const issueRepo = stateManager.getIssueRepo();

      issueRepo.updateRetryCount(issue.id, 0);
      issueRepo.updateBlockedReason(issue.id, null);
      issueRepo.clearApprovalState(issue.id);

      let hasCheckpoint = false;
      let checkpointStage: string | null = null;

      if (worktreeManager && checkpointRepo) {
        const project = projectService.getById(projectId);
        if (project) {
          const wtPath = worktreeManager.getPath(project.name, number);
          if (wtPath) {
            const changeDir = findChangeDir(wtPath, number);
            if (changeDir) {
              const tasksPath = path.join(changeDir, 'tasks.json');
              if (fs.existsSync(tasksPath)) {
                hasCheckpoint = true;
                checkpointStage = issue.stage;
              }
            }
          }
        }
      }

      if (hasCheckpoint && checkpointStage && agentRunner) {
        issueRepo.updateStatus(issue.id, IssueStatus.Active);
        issueRepo.updateBlockedReason(issue.id, null);

        const result = agentRunner.enqueue(issue.id, 'resume-pipeline');
        return c.json({
          success: true,
          data: {
            taskId: result.taskId,
            status: result.status,
            queuePosition: result.queuePosition,
            message: `Issue #${number} retrying from checkpoint (${checkpointStage})`,
          },
        } satisfies ApiResponse, 202);
      }

      issueService.transitionToStage(issue.id, Stage.Backlog);
      issueRepo.updateStatus(issue.id, IssueStatus.Active);

      return c.json({
        success: true,
        data: { message: `Issue #${number} retrying — no checkpoint found, reset to draft stage. Use start to begin again.` },
      } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
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
        return c.json({ success: false, error: `Issue #${number} is not blocked (current: ${issue.status})` } satisfies ApiResponse, 409);
      }

      if (agentRunner) {
        const restartQueueStatus = agentRunner.getQueueStatus(issue.id) as IssueQueueStatus;
        if (restartQueueStatus.running) {
          return c.json({ success: false, error: `Issue #${number} already has a running task` } satisfies ApiResponse, 409);
        }
        if (restartQueueStatus.pending.length > 0) {
          agentRunner.cancelAll(issue.id);
        }
      }

      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateRetryCount(issue.id, 0);
      issueRepo.updateBlockedReason(issue.id, null);
      issueRepo.clearApprovalState(issue.id);
      issueService.transitionToStage(issue.id, Stage.Backlog);
      issueRepo.updateStatus(issue.id, IssueStatus.Active);

      return c.json({
        success: true,
        data: { message: `Issue #${number} reset to draft stage. Use start to begin again.` },
      } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
    }
  });

  app.post('/:number/rerun', async (c) => {
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

      if (issue.stage === Stage.Draft || issue.stage === Stage.Backlog) {
        return c.json({ success: false, error: `Issue #${number} is in ${issue.stage} stage. Use start instead of rerun.` } satisfies ApiResponse, 400);
      }

      if (issue.stage === Stage.Done) {
        return c.json({ success: false, error: `Issue #${number} is in done stage. Rerun is not supported for completed issues.` } satisfies ApiResponse, 400);
      }

      if (!agentRunner) {
        return c.json({ success: false, error: 'AgentRunnerService not configured' } satisfies ApiResponse, 500);
      }

      const project = projectService.getById(projectId);
      if (!project) {
        return c.json({ success: false, error: 'Project not found' } satisfies ApiResponse, 404);
      }

      if (coderSessionRepo) {
        coderSessionRepo.failRunningByIssueId(issue.id);
      }

      if (checkpointRepo) {
        checkpointRepo.delete(issue.number, issue.stage);
      }

      const issueRepo = stateManager.getIssueRepo();
      issueRepo.clearApprovalState(issue.id);
      issueRepo.updateBlockedReason(issue.id, null);
      issueRepo.updateRetryCount(issue.id, 0);
      issueRepo.updateStatus(issue.id, IssueStatus.Active);

      const result = agentRunner.enqueue(issue.id, 'resume-pipeline');

      return c.json({
        success: true,
        data: {
          issue: issueService.getByNumber(projectId, number),
          taskId: result.taskId,
          status: result.status,
          queuePosition: result.queuePosition,
          message: `Issue #${number} rerun from ${issue.stage} stage`,
        },
      } satisfies ApiResponse, 202);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
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
        return c.json({ success: false, error: `Issue #${number} is not blocked` } satisfies ApiResponse, 409);
      }

      if (agentRunner) {
        const queueStatus = agentRunner.getQueueStatus(issue.id) as IssueQueueStatus;
        if (queueStatus.running) {
          return c.json({ success: false, error: `Issue #${number} already has an agent running` } satisfies ApiResponse, 409);
        }
      }

      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateBlockedReason(issue.id, null);
      issueRepo.updateRetryCount(issue.id, 0);

      const project = projectService.getById(projectId);
      let hasCheckpoint = false;

      if (worktreeManager && project) {
        const worktreePath = worktreeManager.getPath(project.name, issue.number);
        if (worktreePath) {
          const change = findChangeDir(worktreePath, issue.number);
          if (change) {
            const tasksPath = path.join(change, 'tasks.json');
            if (fs.existsSync(tasksPath)) {
              hasCheckpoint = true;
            }
          }
        }
      }

      if (hasCheckpoint && agentRunner && project) {
        issueRepo.updateStatus(issue.id, IssueStatus.Active);
        agentRunner.enqueue(issue.id, 'resume-pipeline');
        return c.json({
          success: true,
          data: { issue: issueService.getByNumber(projectId, number), message: `Issue #${number} retrying from checkpoint` },
        } satisfies ApiResponse);
      }

      issueRepo.updateStage(issue.id, Stage.Backlog);
      issueRepo.updateStatus(issue.id, IssueStatus.Active);

      return c.json({
        success: true,
        data: { issue: issueService.getByNumber(projectId, number), message: `Issue #${number} no checkpoint found, reset to draft. Use start to begin again.` },
      } satisfies ApiResponse);
    } catch (error) {
      return c.json({ success: false, error: error instanceof Error ? error.message : 'Unknown error' } satisfies ApiResponse, 500);
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
        return c.json({ success: false, error: `Issue #${number} is not blocked` } satisfies ApiResponse, 409);
      }

      if (agentRunner) {
        const queueStatus = agentRunner.getQueueStatus(issue.id) as IssueQueueStatus;
        if (queueStatus.running) {
          return c.json({ success: false, error: `Issue #${number} already has an agent running` } satisfies ApiResponse, 409);
        }
      }

      const issueRepo = stateManager.getIssueRepo();
      issueRepo.updateBlockedReason(issue.id, null);
      issueRepo.updateRetryCount(issue.id, 0);
      issueRepo.clearApprovalState(issue.id);
      issueRepo.updateStage(issue.id, Stage.Backlog);
      issueRepo.updateStatus(issue.id, IssueStatus.Active);

      return c.json({
        success: true,
        data: { issue: issueService.getByNumber(projectId, number), message: `Issue #${number} reset to draft. Use start to begin again.` },
      } satisfies ApiResponse);
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
