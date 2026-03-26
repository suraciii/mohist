import { Router, Request, Response } from 'express';
import { StateManager } from '../server/state-manager';
import { TaskQueue } from '../server/task-queue';
import { ApiResponse, Issue, Stage, Comment } from '../types';
import { IssueService, WorkflowService } from '../services';

export function createIssueRoutes(
  stateManager: StateManager,
  taskQueue: TaskQueue
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

  router.post('/:number/start', (req: Request, res: Response): void => {
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

      const result = workflowService.startProcessing(projectId, number);
      
      if (!result.success) {
        const response: ApiResponse = {
          success: false,
          error: result.error
        };
        res.status(400).json(response);
        return;
      }

      const issue = result.issue!;
      const taskId = taskQueue.enqueue(number, projectId, issue.stage);

      const response: ApiResponse = {
        success: true,
        data: { taskId, issue }
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
        const taskId = taskQueue.enqueue(number, projectId, issue.stage);
        const response: ApiResponse = {
          success: true,
          data: { issue, taskId, message: `Issue #${number} approved, continuing to ${issue.stage}` }
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

  router.post('/:number/pause', (req: Request, res: Response): void => {
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

      const issue = issueService.pause(projectId, number);
      
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
        data: { issue, message: `Issue #${number} paused` }
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
        data: { issue, message: `Issue #${number} resumed` }
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

  return router;
}
