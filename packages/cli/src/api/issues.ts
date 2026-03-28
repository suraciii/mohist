import { Router, Request, Response } from 'express';
import { StateManager } from '../server/state-manager';
import { ApiResponse, Issue, Stage, IssueStatus, Comment } from '../types';
import { IssueService, WorkflowService } from '../services';
import { WorktreeManager } from '../git/worktree-manager';
import { SessionManager, type LlmConfig } from '../agent-runtime';
import { runMainAgent } from '../agents/main-agent';

let activeAgentIssueId: string | null = null;
let activeAgentPromise: Promise<void> | null = null;

export function createIssueRoutes(
  stateManager: StateManager,
  worktreeManager: WorktreeManager | null = null,
  sessionManager: SessionManager = new SessionManager(),
  llmConfig?: LlmConfig
): Router {
  const router = Router();
  
  const issueService = new IssueService(
    stateManager.getIssueRepo(),
    stateManager.getTaskRepo()
  );
  const workflowService = new WorkflowService(issueService);

  const getCurrentProjectId = (): string | null => {
    return stateManager.getCurrentProjectId();
  };

  router.get('/', (req: Request, res: Response): void => {
    try {
      const projectId = getCurrentProjectId();
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: mo project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      const stage = req.query.stage as Stage | undefined;
      const label = req.query.label as string | undefined;
      
      let issues = stage 
        ? issueService.getByStage(projectId, stage)
        : issueService.getByProject(projectId);
      
      if (label) {
        issues = issues.filter(issue => issue.labels.includes(label));
      }

      const project = stateManager.getProjectById(projectId);
      const issuesWithProject = issues.map(issue => ({
        ...issue,
        projectName: project?.name || 'unknown'
      }));

      const response: ApiResponse = {
        success: true,
        data: issuesWithProject
      };
      res.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.post('/', (req: Request, res: Response): void => {
    try {
      const { title, body, labels } = req.body;
      
      if (!title) {
        const response: ApiResponse = {
          success: false,
          error: 'title is required'
        };
        res.status(400).json(response);
        return;
      }

      const projectId = getCurrentProjectId();
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: mo project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      const issue = stateManager.createIssue(projectId, title, body, labels);

      const response: ApiResponse<Issue> = {
        success: true,
        data: issue
      };
      res.status(201).json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.get('/:number', (req: Request, res: Response): void => {
    try {
      const number = parseInt(req.params.number);
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: mo project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      const issue = issueService.getByNumber(projectId, number);
      
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        res.status(404).json(response);
        return;
      }

      const comments = stateManager.getCommentsByIssue(issue.id);
      const progress = workflowService.getProgress(issue.stage);
      const stageInfo = workflowService.getStageInfo(issue.stage);
      const project = stateManager.getProjectById(projectId);

      const response: ApiResponse = {
        success: true,
        data: {
          ...issue,
          projectName: project?.name || 'unknown',
          projectPath: project?.path || '',
          comments,
          progress,
          stageInfo
        }
      };
      res.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.patch('/:number', (req: Request, res: Response): void => {
    try {
      const number = parseInt(req.params.number);
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: mo project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      const issue = stateManager.getIssueByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        res.status(404).json(response);
        return;
      }

      const { title, body, addLabels, removeLabels } = req.body;
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

      stateManager.getIssueRepo().update(issue.id, updateData);
      const updated = stateManager.getIssueByNumber(projectId, number);

      const response: ApiResponse<Issue> = {
        success: true,
        data: updated || undefined
      };
      res.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.post('/:number/comments', (req: Request, res: Response): void => {
    try {
      const number = parseInt(req.params.number);
      const { body } = req.body;
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: mo project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      if (!body) {
        const response: ApiResponse = {
          success: false,
          error: 'body is required'
        };
        res.status(400).json(response);
        return;
      }

      const issue = stateManager.getIssueByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        res.status(404).json(response);
        return;
      }

      const comment = stateManager.createComment(issue.id, body);

      const response: ApiResponse<Comment> = {
        success: true,
        data: comment
      };
      res.status(201).json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.post('/:number/start', async (req: Request, res: Response): Promise<void> => {
    try {
      const number = parseInt(req.params.number);
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: mo project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      if (activeAgentPromise) {
        const response: ApiResponse = {
          success: false,
          error: `Another issue (#${activeAgentIssueId}) is already running. Wait for it to complete or pause first.`
        };
        res.status(400).json(response);
        return;
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        res.status(404).json(response);
        return;
      }

      if (issue.status === 'paused') {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is paused. Resume it first.`
        };
        res.status(400).json(response);
        return;
      }

      if (issue.stage !== Stage.Draft) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} is not in draft stage (current: ${issue.stage})`
        };
        res.status(400).json(response);
        return;
      }

      stateManager.updateIssueStage(issue.id, Stage.Designing);
      const updatedIssue = stateManager.getIssueByNumber(projectId, number)!;

      const project = stateManager.getProjectById(projectId);
      let worktreePath = process.cwd();
      if (worktreeManager && project) {
        worktreePath = await worktreeManager.create(project.path, project.name, issue.number);
      }

      activeAgentIssueId = issue.id;
      activeAgentPromise = (async () => {
        try {
          await runMainAgent(
            {
              id: issue.id,
              number: issue.number,
              title: issue.title,
              body: issue.body,
            },
            {
              issueRepo: stateManager.getIssueRepo(),
              commentRepo: stateManager.getCommentRepo(),
              worktreePath,
              llmConfig,
            },
            sessionManager,
          );
        } catch (err) {
          console.error(`Agent loop failed for issue #${number}:`, err);
          stateManager.updateIssueStatus(issue.id, IssueStatus.Blocked);
        } finally {
          activeAgentPromise = null;
          activeAgentIssueId = null;
        }
      })();

      const response: ApiResponse = {
        success: true,
        data: { issue: updatedIssue, message: `Issue #${number} started, agent is running` }
      };
      res.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.post('/:number/approve', (req: Request, res: Response): void => {
    try {
      const number = parseInt(req.params.number);
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: mo project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      const result = workflowService.approve(projectId, number);
      
      if (!result.success) {
        const response: ApiResponse = {
          success: false,
          error: result.error
        };
        res.status(400).json(response);
        return;
      }

      const issue = result.issue!;
      
      if (issue.stage !== Stage.Done) {
        const task = stateManager.createTask(issue.id, projectId, issue.stage);
        const response: ApiResponse = {
          success: true,
          data: { issue, taskId: task.id, message: `Issue #${number} approved, continuing to ${issue.stage}` }
        };
        res.json(response);
      } else {
        const response: ApiResponse = {
          success: true,
          data: { issue, message: `Issue #${number} completed!` }
        };
        res.json(response);
      }
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.post('/:number/pause', (_req: Request, res: Response): void => {
    const response: ApiResponse = {
      success: false,
      error: 'Pause is not supported in M1. Stop the mo-server process to halt a running agent.'
    };
    res.status(501).json(response);
  });

  router.post('/:number/resume', (req: Request, res: Response): void => {
    try {
      const number = parseInt(req.params.number);
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: mo project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      const issue = issueService.getByNumber(projectId, number);
      
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        res.status(404).json(response);
        return;
      }

      const resumeableStages = [Stage.Designing, Stage.Implementing, Stage.WaitingDesignReview];
      if (!resumeableStages.includes(issue.stage)) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} cannot be resumed from stage "${issue.stage}". Must be in designing, implementing, or waiting-design-review stage.`
        };
        res.status(400).json(response);
        return;
      }

      const taskRepo = stateManager.getTaskRepo();
      const existingTasks = taskRepo.findByIssueId(issue.id);
      const hasActiveTask = existingTasks.some(
        t => t.status === 'running' || t.status === 'pending'
      );
      if (hasActiveTask) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} already has an active task. Wait for it to complete or pause first.`
        };
        res.status(400).json(response);
        return;
      }

      const resumed = issueService.resume(projectId, number);
      if (!resumed) {
        const response: ApiResponse = {
          success: false,
          error: `Failed to resume Issue #${number}`
        };
        res.status(500).json(response);
        return;
      }

      let task;
      if (issue.stage === Stage.WaitingDesignReview) {
        issueService.transitionToStage(resumed.id, Stage.Designing);
        task = stateManager.createTask(issue.id, projectId, Stage.Designing);
      } else {
        task = stateManager.createTask(issue.id, projectId, issue.stage);
      }

      const response: ApiResponse = {
        success: true,
        data: { issue: resumed, taskId: task.id, message: `Issue #${number} resumed` }
      };
      res.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.post('/:number/close', (req: Request, res: Response): void => {
    try {
      const number = parseInt(req.params.number);
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: mo project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      const issue = issueService.block(projectId, number);
      
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        res.status(404).json(response);
        return;
      }

      const response: ApiResponse = {
        success: true,
        data: { issue, message: `Issue #${number} closed` }
      };
      res.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.post('/:number/reopen', (req: Request, res: Response): void => {
    try {
      const number = parseInt(req.params.number);
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: mo project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      const issue = issueService.resume(projectId, number);
      
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        res.status(404).json(response);
        return;
      }

      const response: ApiResponse = {
        success: true,
        data: { issue, message: `Issue #${number} reopened` }
      };
      res.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  router.post('/:number/cleanup', async (req: Request, res: Response): Promise<void> => {
    try {
      const number = parseInt(req.params.number);
      const projectId = getCurrentProjectId();

      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: mo project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      const issue = issueService.getByNumber(projectId, number);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${number} not found`
        };
        res.status(404).json(response);
        return;
      }

      const project = stateManager.getProjectById(projectId);

      if (worktreeManager && project) {
        await worktreeManager.remove(project.path, project.name, issue.number);
      }

      const response: ApiResponse = {
        success: true,
        data: { issue, message: `Issue #${number} worktree cleaned up` }
      };
      res.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error'
      };
      res.status(500).json(response);
    }
  });

  return router;
}
