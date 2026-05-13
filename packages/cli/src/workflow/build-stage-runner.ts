import * as fs from 'fs';
import { Stage } from '../types';
import type { TasksFile } from '../artifacts/change-artifacts-manager';
import { detectOpenSpecChange } from '../openspec/detector';
import { RalphExecutor, readTasks, type RalphLoopResult } from '../openspec/ralph-executor';
import { loadWorkflow, loadHealthGatePolicies } from './workflow-loader';
import { GitCommitter } from './git-committer';
import { BaseStageRunner } from './base-stage-runner';
import type { CheckResult, StageContext, StageRunResult, StageTaskResult } from './stage-context';
import type { Check } from './checks';
import { HealthGateCheck } from './checks/health-gate-check';
import { Log } from '../util/log';
import { createWorkflowSessionObservers } from '../agent-runtime';
import { runHealthFixTask } from './health-fix-task';

const log = Log.create({ service: 'workflow' });

export class BuildStageRunner extends BaseStageRunner {
  private worktreePath: string;
  private projectId?: string;
  private gitCommitter: GitCommitter;
  private buildHealthGatePolicy: import('./workflow-loader').HealthGatePolicy;

  constructor(opts: { worktreePath: string; projectId?: string }) {
    super();
    this.worktreePath = opts.worktreePath;
    this.projectId = opts.projectId;
    this.gitCommitter = new GitCommitter(this.worktreePath);
    const wf = loadWorkflow(this.worktreePath);
    this.buildHealthGatePolicy = typeof wf === 'string'
      ? { enabled: true, command: 'npm run build', timeout: 300000, autoFix: true, maxFixAttempts: 2, fallbackReaction: { type: 'escalate', escalateTarget: Stage.Build } }
      : loadHealthGatePolicies(wf).build;
  }

  canHandle(stage: Stage): boolean {
    return stage === Stage.Build;
  }

  protected async executeTasks(ctx: StageContext): Promise<unknown> {
    const { issue, acpOptions, checkpointManager } = ctx;
    const issueId = issue.id;
    const projectId = this.projectId ?? issue.projectId;

    const completedTaskIds = checkpointManager.getResumeSteps(issue.number, 'build');

    const change = detectOpenSpecChange(this.worktreePath, issue);

    if (completedTaskIds.length > 0) {
      log.info('Build stage resuming', {
        issueNumber: issue.number,
        completedTaskIds,
      });
    }

    if (!change) {
      log.warn('detectOpenSpecChange returned null', {
        worktreePath: this.worktreePath,
        issueNumber: issue.number,
      });

      ctx.emit('build_stage_failed', {
        issueId,
        projectId,
        reason: 'no_change_found',
        details: { worktreePath: this.worktreePath },
        timestamp: new Date().toISOString(),
      });
      ctx.log('build_failed', {
        reason: 'no_change_found',
        worktreePath: this.worktreePath,
        issueNumber: issue.number,
      });

      throw new Error(`No OpenSpec change found for issue #${issue.number}`);
    }

    log.info('detectOpenSpecChange found change', {
      changePath: change.changePath,
      tasksPath: change.tasksPath,
      issueNumber: issue.number,
    });

    let total = 0;
    let pending = 0;
    let passed = 0;
    let taskSnapshot: TasksFile['tasks'] = [];

    try {
      const tasksContent = fs.readFileSync(change.tasksPath, 'utf-8');
      const tasksFile = JSON.parse(tasksContent) as TasksFile;
      taskSnapshot = tasksFile.tasks;
      total = taskSnapshot.length;
      pending = taskSnapshot.filter(t => !t.passes).length;
      passed = taskSnapshot.filter(t => t.passes).length;
    } catch {
      log.warn('Failed to read tasks snapshot for build stage logging', {
        tasksPath: change.tasksPath,
        issueNumber: issue.number,
      });
    }

    if (ctx.workflowApplicationService) {
      const buildTasks = readTasks(change.tasksPath) ?? [];
      ctx.workflowApplicationService.materializeTasks({
        issueId: issue.id,
        stage: Stage.Build,
        tasks: buildTasks.map(task => ({
          id: task.id,
          title: task.title,
          order: task.order,
          dependsOn: task.dependsOn ?? [],
        })),
        tasksPath: change.tasksPath,
      });
    } else {
      for (const t of taskSnapshot) {
        if (t.passes && !completedTaskIds.includes(t.id)) {
          completedTaskIds.push(t.id);
        }
      }
    }

    log.info('Build stage tasks snapshot', {
      issueNumber: issue.number,
      total,
      pending,
      passed,
    });

    ctx.emit('build_stage_started', {
      issueId,
      projectId,
      stage: 'build' as const,
      changePath: change.changePath,
      tasksCount: total,
      timestamp: new Date().toISOString(),
    });
    ctx.emit('build_tasks_snapshot', {
      issueId,
      projectId,
      total,
      pending,
      passed,
    });
    ctx.log('build_started', {
      changePath: change.changePath,
      tasksCount: total,
      pending,
      passed,
    });

    const executorObservers = createWorkflowSessionObservers({
      eventBus: ctx.eventBus,
      workflowLogRepo: ctx.workflowLogRepo,
      sessionStreamLogRepo: ctx.sessionStreamLogRepo,
      coderSessionRepo: ctx.coderSessionRepo,
      stage: 'build',
      title: 'Build stage',
    });

    const executor = new RalphExecutor({
      worktreePath: this.worktreePath,
      projectPath: this.worktreePath,
      issueId: issue.id,
      projectId: issue.projectId,
      eventBus: ctx.eventBus,
      executionId: `build-${issue.number}`,
      issueNumber: issue.number,
      issueTitle: issue.title,
      issueBody: issue.body,
      stageTimeoutMs: this.getBuildStageTimeoutMs(),
      stageExecutionId: this.getStageExecutionId(),
      stageExecutionRepo: ctx.stageExecutionRepo,
      model: acpOptions.model,
      stage: 'build',
      observers: executorObservers,
      workflowApplicationService: ctx.workflowApplicationService,
      syncTasksToStageState: () => {
        if (!ctx.stageStateService) return;
        ctx.artifactManager.syncTasksToStageState(issue.number, issue.id, Stage.Build, ctx.stageStateService);
      },
    });

    const activeCompletedTaskIds = [...completedTaskIds];
    const requestedAggregateTaskId = ctx.workflowApplicationService && ctx.requestedWork?.kind === 'task'
      ? ctx.requestedWork.taskId
      : undefined;

    const result: RalphLoopResult = await executor.execute(change, {
      skipTaskIds: requestedAggregateTaskId ? undefined : (completedTaskIds.length > 0 ? completedTaskIds : undefined),
      ignoreTaskFileProgress: Boolean(ctx.workflowApplicationService),
      onlyTaskId: requestedAggregateTaskId,
      onTaskCompleted: (taskId: string) => {
        activeCompletedTaskIds.push(taskId);
        checkpointManager.markStepComplete(issue.number, 'build', taskId);
      },
    });

    log.info('Ralph loop completed', {
      issueNumber: issue.number,
      completed: result.completed,
      failed: result.failed,
      total: result.total,
      success: result.success,
    });

    const hadCheckpoint = activeCompletedTaskIds.length > 0;

    if (result.completed === 0 && result.total > 0 && !hadCheckpoint) {
      log.warn('Build completed with 0 tasks executed out of total', {
        total: result.total,
        issueNumber: issue.number,
      });

      ctx.emit('build_stage_failed', {
        issueId,
        projectId,
        reason: 'zero_work',
        details: { completed: result.completed, total: result.total },
        timestamp: new Date().toISOString(),
      });
      ctx.log('build_failed', {
        reason: 'zero_work',
        completed: result.completed,
        total: result.total,
      });

      throw new Error(
        `Build completed with 0 tasks executed out of ${result.total} total — tasks may have been pre-marked as passed`,
      );
    }

    if (result.success) {
      await this.gitCommitter.commitBuildChanges(issue);
      if (!ctx.requestedWork) {
        checkpointManager.delete(issue.number, 'build');
      }

      ctx.emit('build_stage_completed', {
        issueId,
        projectId,
        completed: result.completed,
        failed: result.failed,
        total: result.total,
        timestamp: new Date().toISOString(),
      });
      ctx.log('build_completed', {
        completed: result.completed,
        failed: result.failed,
        total: result.total,
      });
    } else {
      ctx.emit('build_stage_failed', {
        issueId,
        projectId,
        reason: 'tasks_failed',
        details: { completed: result.completed, failed: result.failed, total: result.total },
        timestamp: new Date().toISOString(),
      });
      ctx.log('build_failed', {
        reason: 'tasks_failed',
        completed: result.completed,
        failed: result.failed,
        total: result.total,
      });
    }

    return {
      stage: Stage.Build,
      issueNumber: issue.number,
      completedTasks: result.completed,
      failedTasks: result.failed,
      totalTasks: result.total,
    };
  }

  protected getChecks(): Check[] {
    return [
      new HealthGateCheck({
        worktreePath: this.worktreePath,
        policy: this.buildHealthGatePolicy,
        stage: 'build',
      }),
    ];
  }

  protected async executeReportedTask(
    ctx: StageContext,
    taskId: string,
    failedCheck: CheckResult | undefined,
    attempt: number,
  ): Promise<StageTaskResult | null> {
    if (taskId !== 'fix-build-health') return null;
    if (!failedCheck) {
      return {
        taskId: 'fix-build-health',
        title: 'Fix build health',
        status: 'failed',
        artifacts: [],
        attempts: attempt,
        duration: 0,
        reason: 'Missing failed check context for fix-build-health',
        output: { error: 'Missing failed check context for fix-build-health' },
      };
    }
    return runHealthFixTask(ctx, {
      taskId: 'fix-build-health',
      title: 'Fix build health',
      stage: 'build',
      worktreePath: this.worktreePath,
      healthCommand: this.buildHealthGatePolicy.command,
      failedCheck,
      attempt,
    });
  }

  protected getNextStage(): Stage {
    return Stage.Check;
  }

  async run(ctx: StageContext): Promise<StageRunResult> {
    if (!ctx.workflowApplicationService) {
      return super.run(ctx);
    }

    if (ctx.requestedWork?.kind === 'task' && ctx.requestedWork.stage === Stage.Build && ctx.requestedWork.taskId !== 'fix-build-health') {
      try {
        ctx.requestedTask ??= {
          id: ctx.requestedWork.taskId,
          title: ctx.requestedWork.taskId,
          status: 'pending',
          order: 0,
          dependsOn: [],
          attempts: 0,
          duration: 0,
          artifacts: [],
          output: null,
          reason: null,
          causedBy: null,
        };
        const output = await this.executeTasks(ctx);
        const taskOutput = output as { completedTasks?: number; failedTasks?: number; totalTasks?: number } | null;
        return {
          success: Boolean(taskOutput && taskOutput.failedTasks === 0 && taskOutput.completedTasks === 1),
          output,
          checkResults: [],
          message: taskOutput?.failedTasks ? `Task ${ctx.requestedWork.taskId} failed` : undefined,
        };
      } catch (err: any) {
        return {
          success: false,
          output: null,
          checkResults: [],
          message: `Task execution failed: ${err.message}`,
        };
      }
    }

    const result = await super.run(ctx);

    return {
      ...result,
      output: result.output ?? {
        stage: Stage.Build,
        issueNumber: ctx.issue.number,
      },
    };
  }

  private getBuildStageTimeoutMs(): number | undefined {
    const config = loadWorkflow(this.worktreePath);
    if (typeof config === 'string') return undefined;
    const buildStage = config.stages.find(s => s.stage === 'build');
    if (!buildStage?.timeout) return undefined;
    return buildStage.timeout * 1000;
  }
}
