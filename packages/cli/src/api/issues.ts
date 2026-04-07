import { Hono } from 'hono';
import { StateManager } from '../server/state-manager';
import { ApiResponse, Issue, Stage, IssueStatus, Comment } from '../types';
import { IssueService } from '../services';
import { ProjectService } from '../services';
import { AgentRunnerService } from '../services';
import { WorktreeManager } from '../git/worktree-manager';
import { SessionManager, type LlmConfig } from '../agent-runtime';
import { WorkflowLogRepo } from '../db/workflow-log-repo';
import { execFile } from 'child_process';
import { promisify } from 'util';

const execFileAsync = promisify(execFile);

export function createIssueRoutes(
  issueService: IssueService,
  projectService: ProjectService,
  stateManager: StateManager,
  worktreeManager: WorktreeManager | null = null,
  sessionManager: SessionManager = new SessionManager(),
  llmConfig?: LlmConfig,
  agentRunner?: AgentRunnerService,
  workflowLogRepo?: WorkflowLogRepo,
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
      
      let issues = stage 
        ? issueService.getByStage(projectId, stage)
        : issueService.getByProject(projectId);
      
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
      const { title, body, labels } = await c.req.json();
      
      if (!title) {
        const response: ApiResponse = {
          success: false,
          error: 'title is required'
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

      const issue = issueService.create({ projectId, title, body, labels });

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

      const { title, body, addLabels, removeLabels } = await c.req.json();
      const updateData: Partial<{ title: string; body: string; labels: string[] }> = {};
      
      if (title !== undefined) updateData.title = title;
      if (body !== undefined) updateData.body = body;
      
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
          error: `Issue #${number} is blocked. Run: mo issue reopen ${number}`
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

      issueService.transitionToStage(issue.id, Stage.Plan);
      const updatedIssue = issueService.getByNumber(projectId, number)!;

      const project = projectService.getById(projectId);
      let worktreePath = process.cwd();
      if (worktreeManager && project) {
        worktreePath = await worktreeManager.create(project.path, project.name, issue.number);
      }

      if (!agentRunner) {
        const response: ApiResponse = {
          success: false,
          error: 'AgentRunnerService not configured'
        };
        return c.json(response, 500);
      }

      if (agentRunner.isRunning(updatedIssue.id)) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} already has an agent running`
        };
        return c.json(response, 409);
      }

      const startResult = agentRunner.start(
        updatedIssue,
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
            message: `Issue #${number} started, agent is running`,
            queuePosition: 0,
            runningAgents: agentRunner.getStatus().activeAgents.length,
          }
        };
        return c.json(response);
      }

      const maxConcurrent = agentRunner.getMaxConcurrentAgents();
      const response: ApiResponse = {
        success: true,
        data: {
          issue: updatedIssue,
          message: `Issue #${number} queued, position: ${startResult.queuePosition}/${maxConcurrent}`,
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

      const blockedIssue = issueService.block(projectId, number);
      if (!blockedIssue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const response: ApiResponse = {
        success: true,
        data: { issue: blockedIssue, message: `Issue #${number} closed` }
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

      const issue = issueService.resume(projectId, number);
      
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        return c.json(response, 404);
      }

      const response: ApiResponse = {
        success: true,
        data: { issue, message: `Issue #${number} reopened` }
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

      if (!agentRunner.hasPausedSession(number)) {
        const response: ApiResponse = {
          success: false,
          error: `No paused session for issue #${number}. The session may have expired due to server restart. Try: mo issue reopen ${number} then mo issue start ${number}`
        };
        return c.json(response, 400);
      }

      const project = projectService.getById(projectId);
      let worktreePath = process.cwd();
      if (worktreeManager && project) {
        const existingPath = worktreeManager.getPath(project.name, issue.number);
        worktreePath = existingPath || process.cwd();
      }

      agentRunner.resume(
        issue,
        projectId,
        stateManager.getIssueRepo(),
        stateManager.getCommentRepo(),
        stateManager.getQuestionRepo(),
        worktreePath,
        sessionManager,
        '[System] User approved. Continue to next stage.',
        llmConfig,
        (issueId, status) => issueService.setStatus(issueId, status),
      );

      const response: ApiResponse = {
        success: true,
        data: {
          issue,
          message: `Issue #${number} approved, agent resumed`
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

      if (!agentRunner.hasPausedSession(number)) {
        const response: ApiResponse = {
          success: false,
          error: `Agent is not paused for issue #${number}`
        };
        return c.json(response, 409);
      }

      const project = projectService.getById(projectId);
      let worktreePath = process.cwd();
      if (worktreeManager && project) {
        const existingPath = worktreeManager.getPath(project.name, issue.number);
        worktreePath = existingPath || process.cwd();
      }

      agentRunner.resume(
        issue,
        projectId,
        stateManager.getIssueRepo(),
        stateManager.getCommentRepo(),
        stateManager.getQuestionRepo(),
        worktreePath,
        sessionManager,
        message,
        llmConfig,
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

      let defaultBranch = 'main';
      try {
        const result = await execFileAsync(
          'git',
          ['symbolic-ref', 'refs/remotes/origin/HEAD'],
          { cwd: project.path }
        );
        defaultBranch = result.stdout.trim().replace('refs/remotes/origin/', '');
      } catch {
        try {
          const result = await execFileAsync(
            'git',
            ['rev-parse', '--abbrev-ref', 'HEAD'],
            { cwd: project.path }
          );
          defaultBranch = result.stdout.trim();
        } catch {
        }
      }

      const branchName = `mo/issue-${number}`;
      const diffOutput = await execFileAsync(
        'git',
        ['diff', `${defaultBranch}...${branchName}`, '--stat'],
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

  return app;
}
