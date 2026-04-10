import { Stage, isValidTransition, type Issue } from '../types';
import type { PrdTask, PrdJson, PrdTaskStatus } from '../artifacts/change-artifacts-manager';
import { executeCoderTask } from '../tools/spawn-coder';
import type { PlanResult, ReviewResult } from '../types/workflow-results';
import { detectOpenSpecChange } from '../openspec/detector';
import { RalphExecutor, type RalphLoopResult } from '../openspec/ralph-executor';

export interface PlannerAgent {
  plan(options: {
    issue: Issue;
    worktreePath: string;
    prompt?: string;
  }): Promise<PlanResult>;
}

export interface ReviewerAgent {
  review(options: {
    issue: Issue;
    worktreePath: string;
    prompt?: string;
  }): Promise<ReviewResult>;
}

export interface ChangeArtifactsManager {
  getChangeDir(issueNumber: number): string | null;
  createChangeDir(issueNumber: number, title: string): string | null;
  readArtifact(changeDir: string, artifactPath: string): string | null;
  writeArtifact(changeDir: string, artifactPath: string, content: string): boolean;
  exists(changeDir: string): boolean;
  readPrd(issueNumber: number): PrdJson | null;
  updateTaskStatus(issueNumber: number, taskId: string, status: PrdTaskStatus): boolean;
}

export interface StageResult {
  success: boolean;
  requiresApproval: boolean;
  output: unknown;
  message?: string;
}

export interface WorkflowControllerOptions {
  plannerAgent: PlannerAgent;
  reviewerAgent: ReviewerAgent;
  artifactManager: ChangeArtifactsManager;
  worktreePath: string;
}

export class WorkflowController {
  private plannerAgent: PlannerAgent;
  private reviewerAgent: ReviewerAgent;
  private artifactManager: ChangeArtifactsManager;
  private worktreePath: string;

  constructor(options: WorkflowControllerOptions) {
    this.plannerAgent = options.plannerAgent;
    this.reviewerAgent = options.reviewerAgent;
    this.artifactManager = options.artifactManager;
    this.worktreePath = options.worktreePath;
  }

  validateTransition(from: Stage, to: Stage): boolean {
    return isValidTransition(from, to);
  }

  async executeStage(issue: Issue, stage: Stage): Promise<StageResult> {
    switch (stage) {
      case Stage.Plan:
        return this.executePlanStage(issue);
      case Stage.Build:
        return this.executeBuildStage(issue);
      case Stage.Review:
        return this.executeReviewStage(issue);
      case Stage.Explore:
        return this.executeExploreStage(issue);
      case Stage.Done:
        return this.executeDoneStage(issue);
      default:
        return {
          success: false,
          requiresApproval: false,
          output: null,
          message: `Unknown stage: ${stage}`,
        };
    }
  }

  private async executeExploreStage(issue: Issue): Promise<StageResult> {
    const changeDir = this.artifactManager.getChangeDir(issue.number);
    const hasExistingChange = changeDir && this.artifactManager.exists(changeDir);

    return {
      success: true,
      requiresApproval: false,
      output: {
        stage: Stage.Explore,
        issueNumber: issue.number,
        existingChangeDir: hasExistingChange ? changeDir : null,
      },
      message: 'Explore stage executed',
    };
  }

  private async executePlanStage(issue: Issue): Promise<StageResult> {
    try {
      const result = await this.plannerAgent.plan({
        issue,
        worktreePath: this.worktreePath,
      });

      return {
        success: result.success,
        requiresApproval: true,
        output: result,
        message: result.success
          ? 'Plan completed, awaiting user approval'
          : 'Plan failed',
      };
    } catch (error) {
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: error instanceof Error ? error.message : 'Plan execution failed',
      };
    }
  }

  private async executeBuildStage(issue: Issue): Promise<StageResult> {
    const change = detectOpenSpecChange(this.worktreePath, issue);

    if (change) {
      const executor = new RalphExecutor({
        worktreePath: this.worktreePath,
        projectPath: this.worktreePath,
        issueId: issue.id,
        projectId: issue.projectId,
      });

      const result: RalphLoopResult = await executor.execute(change);

      return {
        success: result.success,
        requiresApproval: result.failed > 0,
        output: {
          stage: Stage.Build,
          issueNumber: issue.number,
          completedTasks: result.completed,
          failedTasks: result.failed,
          totalTasks: result.total,
        },
        message: result.success
          ? `Build completed - ${result.completed}/${result.total} tasks executed`
          : `Build completed with ${result.failed} failed task(s)`,
      };
    }

    return this.executeBuildStageFallback(issue);
  }

  private async executeBuildStageFallback(issue: Issue): Promise<StageResult> {
    const MAX_RETRIES = 3;

    const prd = this.artifactManager.readPrd(issue.number);
    if (!prd) {
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: `No prd.json found for issue #${issue.number}. Cannot execute Build phase.`,
      };
    }

    const tasks = prd.tasks || [];
    if (tasks.length === 0) {
      return {
        success: true,
        requiresApproval: false,
        output: { stage: Stage.Build, issueNumber: issue.number, completedTasks: 0 },
        message: 'Build phase completed - no tasks to execute',
      };
    }

    const taskResults: Array<{ taskId: string; success: boolean; attempts: number; error?: string }> = [];

    for (const task of tasks) {
      const taskId = task.id || `task-${taskResults.length}`;

      if (task.status === 'completed') {
        console.log(`[Build phase] Skipping task "${task.title}" (${taskId}) - already completed`);
        taskResults.push({ taskId, success: true, attempts: task.attempts || 1 });
        continue;
      }

      const currentAttempts = task.attempts || 0;
      let attempt = currentAttempts;
      let taskSuccess = false;
      let lastError: string | undefined;

      this.artifactManager.updateTaskStatus(issue.number, taskId, {
        status: 'in_progress',
        startedAt: new Date().toISOString(),
        attempts: attempt + 1,
      });

      while (attempt < MAX_RETRIES + currentAttempts && !taskSuccess) {
        attempt++;
        console.log(`[Build phase] Executing task "${task.title}" (${taskId}), attempt ${attempt}/${MAX_RETRIES + currentAttempts}`);

        const taskPrompt = this.buildTaskPrompt(issue, task);

        const result = await executeCoderTask(this.worktreePath, taskPrompt, {
          issueId: issue.id,
          projectId: issue.projectId,
        });

        if (result.success) {
          taskSuccess = true;
          console.log(`[Build phase] Task "${task.title}" (${taskId}) succeeded on attempt ${attempt}`);
        } else {
          lastError = result.error || 'Unknown error';
          console.warn(`[Build phase] Task "${task.title}" (${taskId}) failed on attempt ${attempt}: ${lastError}`);
        }
      }

      taskResults.push({
        taskId,
        success: taskSuccess,
        attempts: attempt - currentAttempts,
        error: taskSuccess ? undefined : lastError,
      });

      const statusUpdate: PrdTaskStatus = {
        status: taskSuccess ? 'completed' : 'failed',
        completedAt: new Date().toISOString(),
        attempts: attempt,
      };
      if (!taskSuccess && lastError) {
        statusUpdate.error = lastError;
      }
      this.artifactManager.updateTaskStatus(issue.number, taskId, statusUpdate);

      if (!taskSuccess) {
        return {
          success: false,
          requiresApproval: true,
          output: {
            stage: Stage.Build,
            issueNumber: issue.number,
            failedTask: { id: taskId, title: task.title, error: lastError },
            completedTasks: taskResults.length - 1,
            totalTasks: tasks.length,
          },
          message: `Task "${task.title}" (${taskId}) failed after ${MAX_RETRIES} attempts. User intervention required.`,
        };
      }
    }

    return {
      success: true,
      requiresApproval: false,
      output: {
        stage: Stage.Build,
        issueNumber: issue.number,
        completedTasks: taskResults.length,
        totalTasks: tasks.length,
        taskResults,
      },
      message: `Build phase completed successfully - ${taskResults.length}/${tasks.length} tasks executed`,
    };
  }

  private buildTaskPrompt(issue: Issue, task: PrdTask): string {
    const lines = [
      `# Task: ${task.title}`,
      '',
      `## Issue`,
      `#${issue.number}: ${issue.title}`,
      '',
    ];

    if (issue.body) {
      lines.push('## Description');
      lines.push(issue.body);
      lines.push('');
    }

    if (task.description) {
      lines.push('## Task Description');
      lines.push(task.description);
      lines.push('');
    }

    if (task.acceptance_criteria && task.acceptance_criteria.length > 0) {
      lines.push('## Acceptance Criteria');
      for (const criterion of task.acceptance_criteria) {
        lines.push(`- ${criterion}`);
      }
      lines.push('');
    }

    if (task.spec_file) {
      lines.push(`## Spec Reference`);
      lines.push(`See: ${task.spec_file}`);
      lines.push('');
    }

    lines.push('## Instructions');
    lines.push('1. Analyze the task requirements carefully');
    lines.push('2. Implement the solution following existing code patterns');
    lines.push('3. Run tests and verify the implementation');
    lines.push('4. Ensure TypeScript compilation passes');
    lines.push('5. Do not commit changes unless explicitly instructed');

    return lines.join('\n');
  }

  private async executeReviewStage(issue: Issue): Promise<StageResult> {
    try {
      const result = await this.reviewerAgent.review({
        issue,
        worktreePath: this.worktreePath,
      });

      return {
        success: result.passed,
        requiresApproval: true,
        output: result,
        message: result.passed
          ? 'Review passed, awaiting user approval'
          : 'Review found issues',
      };
    } catch (error) {
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: error instanceof Error ? error.message : 'Review execution failed',
      };
    }
  }

  private async executeDoneStage(issue: Issue): Promise<StageResult> {
    return {
      success: true,
      requiresApproval: false,
      output: { stage: Stage.Done, issueNumber: issue.number },
      message: 'Issue completed',
    };
  }
}

export function createWorkflowController(options: WorkflowControllerOptions): WorkflowController {
  return new WorkflowController(options);
}