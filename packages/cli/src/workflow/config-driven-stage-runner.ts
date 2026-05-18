import { Stage, MergeState } from '../types';
import type { StageContext, StageRunResult, StageTaskResult, CheckResult } from './stage-context';
import type { StageRunner } from './check-stage-runner';
import type { CheckContext } from './checks';
import { resolveCheck } from './checks/check-registry';
import type { TaskHandlerRegistry, ExecutableTask, TaskKind } from './task-runtime';
import type { TaskLoaderRegistry } from './task-runtime/task-loader-registry';
import type { TaskDispatchFactoryRegistry, DispatchableTask } from './task-runtime/task-dispatch-factory-registry';
import { createDefaultTaskDispatchFactoryRegistry } from './task-runtime/task-dispatch-factory-registry';
import type { CheckRegistry } from './checks/check-registry';
import type { StageDefinition, TaskExecutionKind, TaskExecutionPolicy, WorkflowDecision, WorkflowEvent, WorkSourceKind } from './domain';
import { Log } from '../util/log';
import { detectOpenSpecChange } from '../openspec/detector';
import { readTasks } from '../openspec/ralph-executor';
import { execFile } from 'child_process';
import { promisify } from 'util';
import * as path from 'path';
import * as fs from 'fs';

const log = Log.create({ service: 'config-driven-stage-runner' });
const execFileAsync = promisify(execFile);

export interface ConfigDrivenStageRunnerOptions {
  taskLoaderRegistry: TaskLoaderRegistry;
  taskHandlerRegistry: TaskHandlerRegistry;
  checkRegistry: CheckRegistry;
  taskDispatchFactoryRegistry?: TaskDispatchFactoryRegistry;
  getStageDefinition(stage: Stage): StageDefinition | undefined;
  worktreePath: string;
  enabledStages?: Stage[];
}

export class ConfigDrivenStageRunner implements StageRunner {
  private taskLoaderRegistry: TaskLoaderRegistry;
  private taskHandlerRegistry: TaskHandlerRegistry;
  private taskDispatchFactoryRegistry: TaskDispatchFactoryRegistry;
  private checkRegistry: CheckRegistry;
  private getStageDefinition: (stage: Stage) => StageDefinition | undefined;
  private worktreePath: string;
  private enabledStages?: Set<Stage>;
  private stageExecutionId?: string;
  private stageExecutionKey?: string;

  constructor(options: ConfigDrivenStageRunnerOptions) {
    this.taskLoaderRegistry = options.taskLoaderRegistry;
    this.taskHandlerRegistry = options.taskHandlerRegistry;
    this.taskDispatchFactoryRegistry = options.taskDispatchFactoryRegistry ?? createDefaultTaskDispatchFactoryRegistry();
    this.checkRegistry = options.checkRegistry;
    this.getStageDefinition = options.getStageDefinition;
    this.worktreePath = options.worktreePath;
    this.enabledStages = options.enabledStages ? new Set(options.enabledStages) : undefined;
  }

  canHandle(stage: Stage): boolean {
    if (this.enabledStages && !this.enabledStages.has(stage)) return false;
    return this.getStageDefinition(stage) !== undefined;
  }

  materializeWork(ctx: StageContext): boolean {
    if (!this.stageNeedsTaskMaterialization(ctx)) return false;
    return this.materializeConfiguredStageTasks(ctx);
  }

  async run(ctx: StageContext): Promise<StageRunResult> {
    if (ctx.requestedWork?.kind === 'task') {
      return this.runRequestedTask(ctx);
    }

    if (ctx.requestedWork?.kind === 'check') {
      return this.runRequestedCheck(ctx);
    }

    return {
      success: false,
      output: null,
      checkResults: [],
      message: 'ConfigDrivenStageRunner requires WorkflowRun requestedWork',
    };
  }

  private appendTaskResult(ctx: StageContext, result: StageTaskResult): WorkflowDecision | null {
    const execId = this.ensureStageExecution(ctx);
    if (execId && ctx.stageExecutionRepo) {
      try {
        ctx.stageExecutionRepo.appendTaskResult(execId, result);
      } catch (e) {
        log.warn('appendTaskResult failed', { error: e instanceof Error ? e.message : String(e) });
      }
    }
    if (ctx.workflowApplicationService) {
      const completion = ctx.workflowApplicationService.completeTask({
        issueId: ctx.issue.id,
        stage: ctx.issue.stage,
        taskId: result.taskId,
        result: {
          status: result.status,
          attempts: result.attempts,
          duration: result.duration,
          artifacts: result.artifacts,
          output: result.output,
          reason: result.reason,
          causedBy: result.causedBy,
        },
      });
      return completion?.decision ?? null;
    }
    return null;
  }

  private async runRequestedTask(ctx: StageContext): Promise<StageRunResult> {
    const work = ctx.requestedWork;
    if (!work || work.kind !== 'task') {
      return { success: false, output: null, checkResults: [], message: 'Invalid requested work' };
    }
    const taskId = work.taskId;
    this.ensureStageExecution(ctx);

    let result: StageTaskResult | null;
    try {
      result = await this.executeTaskWork(ctx, taskId, {
        failedCheck: this.failedCheckForRequestedTask(ctx),
        attempt: (ctx.requestedTask?.attempts ?? 0) + 1,
      });
    } catch (err) {
      this.updateStageExecutionStatus(ctx, 'failed');
      return {
        success: false,
        output: null,
        checkResults: [],
        message: err instanceof Error ? err.message : String(err),
      };
    }

    if (!result) {
      return {
        success: false,
        output: null,
        checkResults: [],
        message: `Task ${taskId} is not executable by ConfigDrivenStageRunner`,
      };
    }

    if (result.status === 'completed') {
      try {
        await this.finalizeSuccessfulTask(ctx, taskId);
      } catch (err) {
        const failedResult: StageTaskResult = {
          ...result,
          status: 'failed',
          reason: err instanceof Error ? err.message : String(err),
        };
        this.appendTaskResult(ctx, failedResult);
        this.updateStageExecutionStatus(ctx, 'failed');
        return {
          success: false,
          output: failedResult.output ?? null,
          checkResults: [],
          message: failedResult.reason,
        };
      }
    }

    const decision = result.alreadyReported ? null : this.appendTaskResult(ctx, result);
    if (result.status === 'completed' && decision) {
      this.applyAcceptedTaskSideEffects(ctx, decision.events);
    }
    if (result.status === 'failed') this.updateStageExecutionStatus(ctx, 'failed');

    return {
      success: result.status !== 'failed',
      output: result.output ?? null,
      checkResults: [],
      message: result.status === 'failed' ? result.reason ?? `Task ${taskId} failed` : undefined,
    };
  }

  private async finalizeSuccessfulTask(ctx: StageContext, taskId: string): Promise<void> {
    if (ctx.issue.stage !== Stage.Plan || taskId !== 'self-review') return;

    const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
    if (!changeDir) return;

    const commitOk = await commitPlanArtifacts(changeDir, ctx.issue);
    if (!commitOk) {
      throw new Error(`Failed to commit plan artifacts for issue #${ctx.issue.number}`);
    }

    ctx.checkpointManager.delete(ctx.issue.number, 'plan');
  }

  private applyAcceptedTaskSideEffects(ctx: StageContext, events: WorkflowEvent[]): void {
    if (ctx.issue.stage !== Stage.Check) return;
    if (!events.some(event => event.type === 'task-invalidated' && event.taskId === 'ai-review')) return;
    this.invalidateReviewArtifactForRereview(ctx);
  }

  private invalidateReviewArtifactForRereview(ctx: StageContext): void {
    const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
    if (!changeDir) return;

    const reviewPath = path.join(changeDir, 'review.md');
    try {
      if (fs.existsSync(reviewPath)) {
        const staleReviewPath = path.join(changeDir, `review.stale-${Date.now()}.md`);
        fs.renameSync(reviewPath, staleReviewPath);
        log.info('Renamed stale review.md before config-driven re-review', { issueNumber: ctx.issue.number, staleReviewPath });
      }
      ctx.checkpointManager?.deleteStep?.(ctx.issue.number, 'check', 'ai-review');
      log.info('Invalidated ai-review checkpoint for config-driven re-review', { issueNumber: ctx.issue.number });
    } catch (err) {
      log.warn('Failed to invalidate review artifact before config-driven re-review', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

  private async runRequestedCheck(ctx: StageContext): Promise<StageRunResult> {
    const work = ctx.requestedWork;
    if (!work || work.kind !== 'check') {
      return { success: false, output: null, checkResults: [], message: 'Invalid requested work' };
    }
    const checkName = work.checkName;
    this.ensureStageExecution(ctx);
    const stageDefinition = this.getStageDefinition(ctx.issue.stage);
    const checkPolicy = stageDefinition?.checkPolicies?.find(policy => policy.checkName === checkName);
    if (!stageDefinition || !checkPolicy) {
      return {
        success: false,
        output: null,
        checkResults: [],
        message: `Check ${checkName} is not declared by stage policy`,
      };
    }

    const checkCtx = this.buildCheckContext(ctx);
    const check = await resolveCheck(this.checkRegistry, checkCtx, checkName);
    const result = await check.run(checkCtx);
    this.recordCheckResult(ctx, result);

    const isPostMergeHealthFailure = ctx.issue.stage === Stage.Integrate
      && checkName === 'health:integrate'
      && result.status === 'fail'
      && this.isPostMerge(ctx);

    return {
      success: result.status === 'pass' || result.status === 'pending',
      output: result.output ?? null,
      checkResults: [result],
      message: result.status === 'pass' || result.status === 'pending'
        ? undefined
        : result.status === 'fail' && isPostMergeHealthFailure
          ? 'post-merge-health-failed'
          : result.message ?? `Check "${checkName}" ${result.status}`,
    };
  }

  private recordCheckResult(ctx: StageContext, result: CheckResult): void {
    const persistedResult = {
      name: result.name,
      status: result.status,
      message: result.message,
      output: result.output,
    };

    const execId = this.ensureStageExecution(ctx);
    if (execId && ctx.stageExecutionRepo) {
      try {
        const repo = ctx.stageExecutionRepo as unknown as { findById?: (id: string) => { checkResults?: unknown[] } | null };
        const current = repo.findById?.(execId);
        ctx.stageExecutionRepo.updateCheckResults(execId, [...(current?.checkResults ?? []), persistedResult]);
      } catch (e) {
        log.warn('updateCheckResults failed', { error: e instanceof Error ? e.message : String(e) });
      }
    }

    const workflowUpdate = ctx.workflowApplicationService
      ? ctx.workflowApplicationService.recordCheckResult({
        issueId: ctx.issue.id,
        stage: ctx.issue.stage,
        result: persistedResult,
      })
      : null;

    this.updateStageExecutionStatus(ctx, this.stageExecutionStatusAfterCheck(ctx, result, workflowUpdate?.decision));
  }

  private ensureStageExecution(ctx: StageContext): string | undefined {
    const key = `${ctx.issue.id}:${ctx.issue.stage}`;
    if (this.stageExecutionId && this.stageExecutionKey === key) return this.stageExecutionId;
    if (!ctx.stageExecutionRepo) return undefined;

    try {
      const active = ctx.stageExecutionRepo.findActiveByIssueId?.(ctx.issue.id);
      if (active?.stage === ctx.issue.stage) {
        this.stageExecutionId = active.id;
        this.stageExecutionKey = key;
        return active.id;
      }

      const execution = ctx.stageExecutionRepo.create(ctx.issue.id, ctx.issue.stage);
      this.stageExecutionId = execution.id;
      this.stageExecutionKey = key;
      return execution.id;
    } catch (e) {
      log.warn('create stage execution failed', { error: e instanceof Error ? e.message : String(e) });
      return undefined;
    }
  }

  private updateStageExecutionStatus(ctx: StageContext, status: 'running' | 'awaiting-approval' | 'passed' | 'failed'): void {
    const execId = this.ensureStageExecution(ctx);
    if (!execId || !ctx.stageExecutionRepo) return;

    try {
      ctx.stageExecutionRepo.updateStatus(execId, status);
    } catch (e) {
      log.warn('updateStageExecutionStatus failed', { error: e instanceof Error ? e.message : String(e) });
    }
  }

  private stageExecutionStatusAfterCheck(
    ctx: StageContext,
    result: CheckResult,
    decision?: import('./domain').WorkflowDecision,
  ): 'running' | 'awaiting-approval' | 'passed' | 'failed' {
    if (decision) {
      if (decision.nextWork.kind === 'task' || decision.nextWork.kind === 'check') return 'running';
      if (decision.nextWork.kind === 'await-approval') return 'awaiting-approval';
      if (decision.nextWork.kind === 'failed') return 'failed';
      if (decision.nextWork.kind === 'complete') return 'passed';
    }

    if (result.status === 'fail' || result.status === 'error') {
      return this.repairAttemptsRemaining(ctx, result.name) ? 'running' : 'failed';
    }
    if (result.status === 'pending') return 'awaiting-approval';
    if (!this.allPolicyChecksPassedAfter(ctx, result)) return 'running';

    const stageDefinition = this.getStageDefinition(ctx.issue.stage);
    return stageDefinition?.approvalPolicy ? 'awaiting-approval' : 'passed';
  }

  private repairAttemptsRemaining(ctx: StageContext, checkName: string): boolean {
    const stageDefinition = this.getStageDefinition(ctx.issue.stage);
    const policy = stageDefinition?.repairPolicies?.find(candidate => candidate.checkName === checkName)
      ?? stageDefinition?.checkFailurePolicies?.find(candidate => candidate.checkName === checkName);
    if (!policy) return false;

    const stageRun = ctx.workflowRun?.stageRuns.find(candidate => candidate.stage === ctx.issue.stage);
    const scheduledFixCount = stageRun?.tasks.filter(task => {
      const causedBy = (task as { causedBy?: { type?: string; checkName?: string } }).causedBy;
      return causedBy?.type === 'check-failure' && causedBy.checkName === checkName;
    }).length ?? 0;
    return scheduledFixCount < policy.maxAttempts;
  }

  private allPolicyChecksPassedAfter(ctx: StageContext, result: CheckResult): boolean {
    const stageDefinition = this.getStageDefinition(ctx.issue.stage);
    const checkPolicies = stageDefinition?.checkPolicies?.filter(policy => policy.phase !== 'approval') ?? [];
    if (checkPolicies.length === 0) return result.status === 'pass';

    const stageRun = ctx.workflowRun?.stageRuns.find(candidate => candidate.stage === ctx.issue.stage);
    return checkPolicies.every(policy => {
      if (policy.checkName === result.name) return result.status === 'pass';
      const snapshot = stageRun?.checks.find(candidate => candidate.checkName === policy.checkName);
      return snapshot?.status === 'passed';
    });
  }

  private isPostMerge(ctx: StageContext): boolean {
    if (ctx.issue.mergeState === MergeState.Merged) return true;
    const stageRun = ctx.workflowRun?.stageRuns.find(candidate => candidate.stage === ctx.issue.stage);
    if (!stageRun) return false;
    const freezePointExists = 'freezePoint' in stageRun && (stageRun as { freezePoint?: unknown }).freezePoint != null;
    return freezePointExists;
  }

  private async executeTaskWork(
    ctx: StageContext,
    taskId: string,
    options: { failedCheck?: CheckResult; attempt?: number } = {},
  ): Promise<StageTaskResult | null> {
    const stageDefinition = this.getStageDefinition(ctx.issue.stage);
    if (!stageDefinition) return null;
    const resolvedTask = this.resolveExecutableTask(ctx, taskId, stageDefinition);
    if (!resolvedTask) return null;

    const dispatchable = this.buildDispatchableTask(ctx, resolvedTask, options);
    if (!dispatchable) return null;

    const handler = this.taskHandlerRegistry.get(dispatchable.kind);
    if (!handler) return null;

    return handler(dispatchable as any, ctx);
  }

  private failedCheckForRequestedTask(ctx: StageContext): CheckResult | undefined {
    const checkName = ctx.requestedTask?.causedBy?.checkName;
    if (!checkName) return undefined;
    const stageRun = ctx.workflowRun?.stageRuns.find(candidate => candidate.stage === ctx.issue.stage);
    const check = stageRun?.checks.find(candidate => candidate.checkName === checkName);
    if (!check) return undefined;
    const status = check.status === 'passed'
      ? 'pass'
      : check.status === 'pending' || check.status === 'running'
        ? 'pending'
        : check.status === 'error'
          ? 'error'
          : 'fail';
    return {
      name: check.checkName,
      status,
      message: check.message ?? ctx.requestedTask?.reason ?? ctx.requestedTask?.causedBy?.message,
      output: check.output ?? undefined,
    };
  }

  private buildCheckContext(ctx: StageContext): CheckContext {
    const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
    return {
      issue: ctx.issue,
      changeDir: changeDir ?? '',
      eventBus: ctx.eventBus,
      projectId: ctx.issue.projectId,
      acpOptions: ctx.acpOptions,
      workflowLogRepo: ctx.workflowLogRepo,
      sessionStreamLogRepo: ctx.sessionStreamLogRepo,
      coderSessionRepo: ctx.coderSessionRepo,
      worktreeManager: ctx.worktreeManager,
      projectRepo: ctx.projectRepo,
    };
  }

  private stageNeedsTaskMaterialization(ctx: StageContext): boolean {
    const stageDefinition = this.getStageDefinition(ctx.issue.stage);
    const hasLoadableWorkSource = stageDefinition?.workSources?.some(workSource => workSource.kind !== 'static' && workSource.kind !== 'runtime') ?? false;
    if (!hasLoadableWorkSource) return false;

    const stageRun = ctx.workflowRun?.stageRuns.find(candidate => candidate.stage === ctx.issue.stage);
    if (!stageRun) return false;

    if (stageRun.stage === Stage.Build) {
      const buildWorkSourceState = (stageRun as { buildWorkSourceState?: { evaluated?: boolean } }).buildWorkSourceState;
      if (!buildWorkSourceState?.evaluated) return true;
    }

    const existingTaskIds = new Set(stageRun.tasks.map(task => this.persistedTaskId(task)));
    return (stageDefinition?.workSources ?? []).some(workSource => {
      if (workSource.kind === 'static' || workSource.kind === 'runtime') return false;
      const change = detectOpenSpecChange(ctx.acpOptions.cwd, ctx.issue);
      if (!change) return false;
      let tasks: Array<{ id: string; title: string; order: number; dependsOn: string[] }> = [];
      try {
        tasks = this.materializeLoadedTasks(ctx, workSource.kind, change);
      } catch {
        return false;
      }
      return tasks.some(task => !existingTaskIds.has(task.id));
    });
  }

  private materializeConfiguredStageTasks(ctx: StageContext): boolean {
    if (!ctx.workflowApplicationService?.materializeTasks) return false;
    const stageDefinition = this.getStageDefinition(ctx.issue.stage);
    if (!stageDefinition?.workSources?.length) return false;

    const stageRun = ctx.workflowRun?.stageRuns.find(candidate => candidate.stage === ctx.issue.stage);
    const existingTaskIds = new Set((stageRun?.tasks ?? []).map(task => this.persistedTaskId(task)));

    let materialized = false;

    for (const workSource of stageDefinition.workSources) {
      if (workSource.kind === 'static' || workSource.kind === 'runtime') continue;

      const change = detectOpenSpecChange(ctx.acpOptions.cwd, ctx.issue);
      if (!change) {
        if (stageRun?.stage === Stage.Build && ctx.workflowApplicationService) {
          ctx.workflowApplicationService.materializeTasks({
            issueId: ctx.issue.id,
            stage: ctx.issue.stage,
            tasks: [],
            buildWorkSourceState: 'missing',
          });
          materialized = true;
        }
        continue;
      }

      let tasks: Array<{ id: string; title: string; order: number; dependsOn: string[] }> = [];
      try {
        tasks = this.materializeLoadedTasks(ctx, workSource.kind, change);
      } catch {
        if (stageRun?.stage === Stage.Build && ctx.workflowApplicationService) {
          ctx.workflowApplicationService.materializeTasks({
            issueId: ctx.issue.id,
            stage: ctx.issue.stage,
            tasks: [],
            buildWorkSourceState: 'invalid',
          });
          materialized = true;
        }
        continue;
      }

      ctx.workflowApplicationService.materializeTasks({
        issueId: ctx.issue.id,
        stage: ctx.issue.stage,
        tasks,
        buildWorkSourceState: tasks.length === 0 ? 'empty' : undefined,
      });
      materialized = true;
      if (tasks.length > 0) {
        for (const task of tasks) existingTaskIds.add(task.id);
      }
    }

    return materialized;
  }

  private materializeLoadedTasks(
    ctx: StageContext,
    kind: 'ralph',
    change: NonNullable<ReturnType<typeof detectOpenSpecChange>>,
  ): Array<{ id: string; title: string; order: number; dependsOn: string[] }> {
    const loader = this.taskLoaderRegistry.get(kind);
    if (!loader) return [];

    const executableTasks = loader.load(ctx);
    if (executableTasks.length === 0) return [];

    const orderedTasks = readTasks(change.tasksPath) ?? [];
    const orderByTaskId = new Map(orderedTasks.map(task => [task.id, task]));

    return executableTasks.map((task, index) => {
      const orderedTask = orderByTaskId.get(task.taskId);
      return {
        id: task.taskId,
        title: task.title,
        order: orderedTask?.order ?? index + 1,
        dependsOn: orderedTask?.dependsOn ?? [],
      };
    });
  }

  private persistedTaskId(task: { id?: string; taskId?: string }): string {
    return task.taskId ?? task.id ?? '';
  }

  private resolveExecutableTask(ctx: StageContext, taskId: string, stageDefinition: StageDefinition): ExecutableTask | null {
    for (const workSource of stageDefinition.workSources ?? []) {
      if (workSource.kind === 'runtime') continue;

      if (workSource.kind === 'static') {
        const allowedTaskIds = new Set(workSource.taskIds ?? stageDefinition.tasks.map(task => task.id));
        if (!allowedTaskIds.has(taskId)) continue;
        const taskDef = this.taskLoaderRegistry.get('static')?.load(ctx).find(task => task.taskId === taskId);
        if (!taskDef) continue;
        const policy = this.resolveTaskExecutionPolicy(stageDefinition, taskId, workSource.kind);
        return {
          ...taskDef,
          taskId: taskDef.taskId,
          title: taskDef.title,
          kind: this.toHandlerKind(policy?.kind ?? taskDef.kind),
        };
      }

      const loader = this.taskLoaderRegistry.get(workSource.kind);
      const executableTask = loader?.load(ctx).find(task => task.taskId === taskId);
      if (executableTask) {
        const policy = this.resolveTaskExecutionPolicy(stageDefinition, taskId, workSource.kind);
        return {
          ...executableTask,
          kind: this.toHandlerKind(policy?.kind ?? executableTask.kind),
        };
      }
    }

    const runtimeTask = this.resolveRuntimeTask(stageDefinition, taskId);
    if (runtimeTask) return runtimeTask;

    return null;
  }

  private resolveTaskExecutionPolicy(
    stageDefinition: StageDefinition,
    taskId: string,
    workSourceKind?: WorkSourceKind,
  ): TaskExecutionPolicy | undefined {
    return stageDefinition.taskExecutionPolicies?.find(policy => {
      const matchesTask = policy.taskId === taskId || policy.taskId === '*';
      const matchesWorkSource = workSourceKind === undefined || policy.workSourceKind === undefined || policy.workSourceKind === workSourceKind;
      return matchesTask && matchesWorkSource;
    });
  }

  private resolveRuntimeTask(stageDefinition: StageDefinition, taskId: string): ExecutableTask | null {
    const baseTaskId = this.baseRuntimeTaskId(taskId);
    const task = stageDefinition.tasks.find(candidate => candidate.id === taskId)
      ?? this.buildRuntimeTaskDefinition(taskId)
      ?? (baseTaskId !== taskId ? this.buildRuntimeTaskDefinition(baseTaskId) : null);
    if (!task) return null;
    const policy = this.resolveTaskExecutionPolicy(stageDefinition, taskId, 'runtime')
      ?? (baseTaskId !== taskId ? this.resolveTaskExecutionPolicy(stageDefinition, baseTaskId, 'runtime') : undefined);
    if (!policy) return null;
    return {
      taskId,
      title: task.title,
      kind: this.toHandlerKind(policy.kind),
    };
  }

  private baseRuntimeTaskId(taskId: string): string {
    return taskId.replace(/:\d+$/, '');
  }

  private buildRuntimeTaskDefinition(taskId: string): { id: string; title: string } | null {
    const runtimeTaskTitles: Record<string, string> = {
      'rebase-branch': 'Rebase branch',
      'fix-plan-review': 'Fix plan review findings',
      'fix-build-health': 'Fix build health',
      'fix-check-health': 'Fix check health',
      'fix-integrate-health': 'Fix integrate health',
      'fix-review-findings': 'Fix review findings',
      'fix-merge-readiness': 'Fix merge readiness',
      'check:converge-review-snapshot': 'Converge review snapshot',
    };
    const title = runtimeTaskTitles[taskId];
    return title ? { id: taskId, title } : null;
  }

  private toHandlerKind(kind: TaskExecutionKind): TaskKind {
    if (kind === 'repair-task' || kind === 'rebase-task') return 'agent-session';
    return kind;
  }

  private buildDispatchableTask(
    ctx: StageContext,
    task: ExecutableTask,
    options: { failedCheck?: CheckResult; attempt?: number } = {},
  ): DispatchableTask | null {
    const stageDefinition = this.getStageDefinition(ctx.issue.stage);
    if (!stageDefinition) return null;
    const attempt = options.attempt ?? 1;
    const workSourceKind = this.taskWorkSourceKind(ctx, stageDefinition, task.taskId);
    const baseTaskId = this.baseRuntimeTaskId(task.taskId);
    const policy = this.resolveTaskExecutionPolicy(stageDefinition, task.taskId, workSourceKind)
      ?? (baseTaskId !== task.taskId ? this.resolveTaskExecutionPolicy(stageDefinition, baseTaskId, workSourceKind) : undefined);
    const executionKind = policy?.kind ?? task.kind;

    return this.taskDispatchFactoryRegistry.build({
      ctx,
      task,
      executionKind,
      attempt,
      failedCheck: options.failedCheck,
      worktreePath: this.worktreePath,
      agentSessionRef: executionKind === 'agent-session' ? policy?.agentSessionRef : undefined,
    });
  }

  private taskWorkSourceKind(ctx: StageContext, stageDefinition: StageDefinition, taskId: string): WorkSourceKind | undefined {
    for (const workSource of stageDefinition.workSources ?? []) {
      if (workSource.kind === 'static') {
        const allowedTaskIds = new Set(workSource.taskIds ?? stageDefinition.tasks.map(task => task.id));
        if (allowedTaskIds.has(taskId)) return workSource.kind;
        continue;
      }

      if (workSource.kind === 'runtime') continue;

      const loader = this.taskLoaderRegistry.get(workSource.kind);
      if (loader?.load(ctx).some(task => task.taskId === taskId)) {
        return workSource.kind;
      }
    }
    return 'runtime';
  }

}

async function commitPlanArtifacts(changeDir: string, issue: { number: number; title: string }): Promise<boolean> {
  try {
    const worktreePath = path.dirname(path.dirname(path.dirname(changeDir)));
    const relPath = path.relative(worktreePath, changeDir);

    const { stdout: statusOut } = await execFileAsync(
      'git',
      ['status', '--porcelain', '--', relPath],
      { cwd: worktreePath },
    );

    if (!statusOut.trim()) {
      log.info('No uncommitted plan artifacts', { issueNumber: issue.number });
      return true;
    }

    await execFileAsync('git', ['add', '--', relPath], { cwd: worktreePath });
    await execFileAsync('git', ['commit', '-m', `plan(issue-${issue.number}): ${issue.title}`], { cwd: worktreePath });

    log.info('Plan artifacts committed', { issueNumber: issue.number, changeDir });
    return true;
  } catch (err) {
    log.warn('Failed to commit plan artifacts', {
      issueNumber: issue.number,
      error: err instanceof Error ? err.message : String(err),
    });
    return false;
  }
}
