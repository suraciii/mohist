import * as fs from 'fs';
import * as path from 'path';
import { Stage, isValidTransition, type Issue } from '../types';
import type { Task, TasksFile } from '../artifacts/change-artifacts-manager';
import { executeCoderTask } from '../tools/spawn-coder';
import type { PlanResult, ReviewResult } from '../types/workflow-results';
import { detectOpenSpecChange } from '../openspec/detector';
import { RalphExecutor, type RalphLoopResult } from '../openspec/ralph-executor';
import { createAcpConnection, type AcpConnection, type AcpConnectionOptions } from '../agent-runtime/acp-session';
import { buildArtifactPrompt, buildSelfReviewPrompt, type ArtifactType } from '../agents/artifact-prompt';
import { Log } from '../util/log';

const log = Log.create({ service: 'workflow' });

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
  readTasks(issueNumber: number): TasksFile | null;
  updateTaskPasses(issueNumber: number, taskId: string, passes: boolean, error?: string | null): boolean;
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

    const tasksFile = this.artifactManager.readTasks(issue.number);
    if (!tasksFile) {
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: `No tasks.json found for issue #${issue.number}. Cannot execute Build phase.`,
      };
    }

    const tasks = tasksFile.tasks || [];
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

      if (task.passes) {
        log.info('Skipping completed task', { issueNumber: issue.number, taskId, taskTitle: task.title });
        taskResults.push({ taskId, success: true, attempts: task.attempts || 1 });
        continue;
      }

      const currentAttempts = task.attempts || 0;
      let attempt = currentAttempts;
      let taskSuccess = false;
      let lastError: string | undefined;

      this.artifactManager.updateTaskPasses(issue.number, taskId, false, undefined);

      while (attempt < MAX_RETRIES + currentAttempts && !taskSuccess) {
        attempt++;
        log.info('Executing task', { issueNumber: issue.number, taskId, taskTitle: task.title, attempt, maxAttempts: MAX_RETRIES + currentAttempts });

        const taskPrompt = this.buildTaskPrompt(issue, task);

        const result = await executeCoderTask(this.worktreePath, taskPrompt, {
          issueId: issue.id,
          projectId: issue.projectId,
        });

        if (result.success) {
          taskSuccess = true;
          log.info('Task succeeded', { issueNumber: issue.number, taskId, taskTitle: task.title, attempt });
        } else {
          lastError = result.error || 'Unknown error';
          log.warn('Task failed', { issueNumber: issue.number, taskId, taskTitle: task.title, attempt, error: lastError });
        }
      }

      taskResults.push({
        taskId,
        success: taskSuccess,
        attempts: attempt - currentAttempts,
        error: taskSuccess ? undefined : lastError,
      });

      this.artifactManager.updateTaskPasses(issue.number, taskId, taskSuccess, taskSuccess ? null : lastError ?? null);

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

  private buildTaskPrompt(issue: Issue, task: Task): string {
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

    if (task.acceptanceCriteria && task.acceptanceCriteria.length > 0) {
      lines.push('## Acceptance Criteria');
      for (const criterion of task.acceptanceCriteria) {
        lines.push(`- ${criterion}`);
      }
      lines.push('');
    }

    if (task.spec) {
      lines.push(`## Spec Reference`);
      lines.push(`See: ${task.spec}`);
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

  async runPlanStage(issue: Issue, acpOptions: AcpConnectionOptions): Promise<StageResult> {
    const changeDir = this.artifactManager.getChangeDir(issue.number);
    if (!changeDir) {
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: `Change directory not found for issue #${issue.number}`,
      };
    }

    cleanChangeDir(changeDir);

    const rounds: PlanRoundConfig[] = [
      { type: 'proposal', verify: () => fs.existsSync(path.join(changeDir, 'proposal.md')), label: 'proposal.md' },
      { type: 'specs', verify: () => fs.existsSync(path.join(changeDir, 'specs')), label: 'specs/' },
      { type: 'design', verify: () => fs.existsSync(path.join(changeDir, 'design.md')), label: 'design.md' },
      { type: 'tasks', verify: () => fs.existsSync(path.join(changeDir, 'tasks.json')), label: 'tasks.json' },
    ];

    let conn: AcpConnection | undefined;

    try {
      conn = await createAcpConnection(acpOptions);

      for (const round of rounds) {
        log.info('Plan stage round', { artifact: round.type, issueNumber: issue.number });

        const prompt = buildArtifactPrompt(round.type as ArtifactType, issue, changeDir);
        const result = await conn.prompt(prompt);

        if (!result.success) {
          log.error('Plan stage round failed', { artifact: round.type, error: result.error });
          await conn.close();
          return {
            success: false,
            requiresApproval: false,
            output: null,
            message: `Plan stage failed at artifact "${round.label}": ${result.error ?? 'unknown error'}`,
          };
        }

        if (!round.verify()) {
          log.error('Plan stage artifact not found after round', { artifact: round.label });
          await conn.close();
          return {
            success: false,
            requiresApproval: false,
            output: null,
            message: `Plan stage failed: artifact "${round.label}" not found after generation`,
          };
        }
      }

      log.info('Plan stage self-review round', { issueNumber: issue.number });
      const selfReviewPrompt = buildSelfReviewPrompt(issue, changeDir);
      const selfReviewResult = await conn.prompt(selfReviewPrompt);

      if (!selfReviewResult.success) {
        log.error('Plan stage self-review failed', { error: selfReviewResult.error });
        await conn.close();
        return {
          success: false,
          requiresApproval: false,
          output: null,
          message: `Plan stage failed at self-review: ${selfReviewResult.error ?? 'unknown error'}`,
        };
      }

      await conn.close();

      return {
        success: true,
        requiresApproval: true,
        output: {
          stage: Stage.Plan,
          issueNumber: issue.number,
          selfReviewNotes: selfReviewResult.text,
        },
        message: 'Plan completed, awaiting user approval',
      };
    } catch (err) {
      if (conn) {
        try {
          await conn.close();
        } catch {
          // ignore cleanup errors
        }
      }
      return {
        success: false,
        requiresApproval: false,
        output: null,
        message: `Plan stage error: ${err instanceof Error ? err.message : String(err)}`,
      };
    }
  }
}

interface PlanRoundConfig {
  type: string;
  verify: () => boolean;
  label: string;
}

function cleanChangeDir(changeDir: string): void {
  if (!fs.existsSync(changeDir)) {
    return;
  }

  const entries = fs.readdirSync(changeDir);
  for (const entry of entries) {
    if (entry === '.openspec.yaml') continue;
    const entryPath = path.join(changeDir, entry);
    fs.rmSync(entryPath, { recursive: true, force: true });
  }
}

export function createWorkflowController(options: WorkflowControllerOptions): WorkflowController {
  return new WorkflowController(options);
}