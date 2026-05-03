import * as fs from 'fs';
import { Stage } from '../types';
import type { TasksFile } from '../artifacts/change-artifacts-manager';
import { detectOpenSpecChange } from '../openspec/detector';
import { RalphExecutor, type RalphLoopResult } from '../openspec/ralph-executor';
import { loadWorkflow } from './workflow-loader';
import { GitCommitter } from './git-committer';
import { BaseStageRunner } from './base-stage-runner';
import type { StageContext, StageRunResult } from './stage-context';
import type { Check } from './checks';
import { AllTasksCompleteCheck } from './checks/all-tasks-complete-check';
import { CodeCompilesCheck } from './checks/code-compiles-check';
import { Log } from '../util/log';

const log = Log.create({ service: 'workflow' });

export class BuildStageRunner extends BaseStageRunner {
  private worktreePath: string;
  private projectId?: string;
  private gitCommitter: GitCommitter;
  private checks: Check[];

  constructor(opts: { worktreePath: string; projectId?: string }) {
    super();
    this.worktreePath = opts.worktreePath;
    this.projectId = opts.projectId;
    this.gitCommitter = new GitCommitter(this.worktreePath);
    this.checks = [
      new AllTasksCompleteCheck(),
      new CodeCompilesCheck({ worktreePath: this.worktreePath }),
    ];
  }

  canHandle(stage: Stage): boolean {
    return stage === Stage.Build;
  }

  protected async executeTasks(ctx: StageContext): Promise<unknown> {
    const { issue, acpOptions, eventBus, checkpointManager } = ctx;
    const issueId = issue.id;
    const projectId = this.projectId ?? issue.projectId;
    const workflowLogRepo = acpOptions.workflowLogRepo;

    const completedTaskIds = checkpointManager.getResumeSteps(issue.number, 'build');

    const change = detectOpenSpecChange(this.worktreePath, issue);

    if (change) {
      try {
        const tasksContent = fs.readFileSync(change.tasksPath, 'utf-8');
        const tasksFile = JSON.parse(tasksContent) as TasksFile;
        for (const t of tasksFile.tasks) {
          if (t.passes && !completedTaskIds.includes(t.id)) {
            completedTaskIds.push(t.id);
          }
        }
      } catch {}
    }

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

      this.emitSafe(eventBus, 'build_stage_failed', {
        issueId,
        projectId,
        reason: 'no_change_found',
        details: { worktreePath: this.worktreePath },
        timestamp: new Date().toISOString(),
      });
      this.writeLog(workflowLogRepo, issueId, 'build_failed', {
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

    try {
      const tasksContent = fs.readFileSync(change.tasksPath, 'utf-8');
      const tasksFile = JSON.parse(tasksContent) as TasksFile;
      const tasks = tasksFile.tasks;
      total = tasks.length;
      pending = tasks.filter(t => !t.passes).length;
      passed = tasks.filter(t => t.passes).length;
    } catch {
      log.warn('Failed to read tasks snapshot for build stage logging', {
        tasksPath: change.tasksPath,
        issueNumber: issue.number,
      });
    }

    log.info('Build stage tasks snapshot', {
      issueNumber: issue.number,
      total,
      pending,
      passed,
    });

    this.emitSafe(eventBus, 'build_stage_started', {
      issueId,
      projectId,
      stage: 'build' as const,
      changePath: change.changePath,
      tasksCount: total,
      timestamp: new Date().toISOString(),
    });
    this.emitSafe(eventBus, 'build_tasks_snapshot', {
      issueId,
      projectId,
      total,
      pending,
      passed,
    });
    this.writeLog(workflowLogRepo, issueId, 'build_started', {
      changePath: change.changePath,
      tasksCount: total,
      pending,
      passed,
    });

    const executor = new RalphExecutor({
      worktreePath: this.worktreePath,
      projectPath: this.worktreePath,
      issueId: issue.id,
      projectId: issue.projectId,
      eventBus,
      executionId: `build-${issue.number}`,
      workflowLogRepo: acpOptions.workflowLogRepo,
      sessionStreamLogRepo: acpOptions.sessionStreamLogRepo,
      coderSessionRepo: acpOptions.coderSessionRepo,
      issueNumber: issue.number,
      stageTimeoutMs: this.getBuildStageTimeoutMs(),
    });

    const activeCompletedTaskIds = [...completedTaskIds];

    const result: RalphLoopResult = await executor.execute(change, {
      skipTaskIds: completedTaskIds.length > 0 ? completedTaskIds : undefined,
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

      this.emitSafe(eventBus, 'build_stage_failed', {
        issueId,
        projectId,
        reason: 'zero_work',
        details: { completed: result.completed, total: result.total },
        timestamp: new Date().toISOString(),
      });
      this.writeLog(workflowLogRepo, issueId, 'build_failed', {
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
      checkpointManager.delete(issue.number, 'build');

      this.emitSafe(eventBus, 'build_stage_completed', {
        issueId,
        projectId,
        completed: result.completed,
        failed: result.failed,
        total: result.total,
        timestamp: new Date().toISOString(),
      });
      this.writeLog(workflowLogRepo, issueId, 'build_completed', {
        completed: result.completed,
        failed: result.failed,
        total: result.total,
      });
    } else {
      this.emitSafe(eventBus, 'build_stage_failed', {
        issueId,
        projectId,
        reason: 'tasks_failed',
        details: { completed: result.completed, failed: result.failed, total: result.total },
        timestamp: new Date().toISOString(),
      });
      this.writeLog(workflowLogRepo, issueId, 'build_failed', {
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
    return this.checks;
  }

  protected getNextStage(): Stage {
    return Stage.Check;
  }

  async run(ctx: StageContext): Promise<StageRunResult> {
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

  private emitSafe(
    eventBus: import('../services/event-bus').EventBus,
    event: string,
    data: unknown,
  ): void {
    if (!eventBus) return;
    try {
      eventBus.emit(event as keyof import('../services/event-bus').EventMap, data as never);
    } catch (e) {
      log.warn('eventBus.emit failed', { event: String(event), error: e instanceof Error ? e.message : String(e) });
    }
  }

  private writeLog(
    workflowLogRepo: import('../db/workflow-log-repo').WorkflowLogRepo | undefined,
    issueId: string,
    eventType: string,
    data: object,
  ): void {
    if (!workflowLogRepo) return;
    try {
      workflowLogRepo.insert(issueId, null, eventType, data);
    } catch (e) {
      log.warn('workflowLogRepo.insert failed', { eventType, issueId, error: e instanceof Error ? e.message : String(e) });
    }
  }
}
