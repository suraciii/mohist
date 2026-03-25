import { Router, Request, Response } from 'express';
import { StateManager } from '../server/state-manager';
import { TaskQueue } from '../server/task-queue';
import { ApiResponse, Issue, Stage } from '../types';
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
          error: 'No current project. Use: crawlph project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      const stage = req.query.stage as Stage | undefined;
      const issues = stage 
        ? issueService.getByStage(projectId, stage)
        : issueService.getByProject(projectId);

      const response: ApiResponse<Issue[]> = {
        success: true,
        data: issues
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
      const { title, body } = req.body;
      
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
          error: 'No current project. Use: crawlph project use <name>'
        };
        res.status(400).json(response);
        return;
      }

      const issue = issueService.create({
        projectId,
        title,
        body,
      });

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
          error: 'No current project. Use: crawlph project use <name>'
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

      const progress = workflowService.getProgress(issue.stage);
      const stageInfo = workflowService.getStageInfo(issue.stage);

      const response: ApiResponse = {
        success: true,
        data: {
          ...issue,
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

  router.post('/:number/start', (req: Request, res: Response): void => {
    try {
      const number = parseInt(req.params.number);
      const projectId = getCurrentProjectId();
      
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No current project. Use: crawlph project use <name>'
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
          error: 'No current project. Use: crawlph project use <name>'
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
          error: 'No current project. Use: crawlph project use <name>'
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
          error: 'No current project. Use: crawlph project use <name>'
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

  return router;
}
