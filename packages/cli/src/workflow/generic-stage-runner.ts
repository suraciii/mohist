import type { StageContext, StageRunResult, StageTaskResult, CheckResult } from './stage-context';
import type { StageRunner } from './stage-runner';
import type { CheckContext } from './checks';
import { resolveCheck } from './checks/check-registry';
import type { TaskHandlerRegistry, ExecutableTask, TaskKind } from './tasks';
import type { TaskLoaderRegistry } from './tasks/task-loader-registry';
import type { TaskDispatchFactoryRegistry, DispatchableTask } from './tasks/task-dispatch-factory-registry';
import { createDefaultTaskDispatchFactoryRegistry } from './tasks/task-dispatch-factory-registry';
import type { CheckRegistry } from './checks/check-registry';
import type { CheckRunStatus, TaskDefinition, TaskRunStatus, WorkflowDecision, WorkflowRun, WorkflowStageId } from './model';
import type { RuntimeStageDefinition, TaskExecutionKind, TaskExecutionPolicy, WorkSourceKind } from './runner/workflow-runtime-definition';
import { inferWorkflowTaskUse } from './uses-catalog';
import { Log } from '../util/log';
import { detectOpenSpecChange } from '../openspec/detector';
import { readTasks } from '../openspec/ralph-executor';

const log = Log.create({ service: 'generic-stage-runner' });
export const GENERIC_STAGE_RUNNER_REQUIRES_WORK_MESSAGE = 'Generic stage runner requires WorkflowRun requestedWork';

type PersistedCheckResult = {
  name: string;
  status: CheckResult['status'];
  message?: string;
  output?: unknown;
};

export interface GenericStageRunnerOptions {
  taskLoaderRegistry: TaskLoaderRegistry;
  taskHandlerRegistry: TaskHandlerRegistry;
  checkRegistry: CheckRegistry;
  taskDispatchFactoryRegistry?: TaskDispatchFactoryRegistry;
  getStageDefinition(stage: WorkflowStageId): RuntimeStageDefinition | undefined;
  worktreePath: string;
  enabledStages?: WorkflowStageId[];
}

export class GenericStageRunner implements StageRunner {
  private taskLoaderRegistry: TaskLoaderRegistry;
  private taskHandlerRegistry: TaskHandlerRegistry;
  private taskDispatchFactoryRegistry: TaskDispatchFactoryRegistry;
  private checkRegistry: CheckRegistry;
  private getStageDefinition: (stage: WorkflowStageId) => RuntimeStageDefinition | undefined;
  private worktreePath: string;
  private enabledStages?: Set<WorkflowStageId>;
  private stageExecutionId?: string;
  private stageExecutionKey?: string;

  constructor(options: GenericStageRunnerOptions) {
    this.taskLoaderRegistry = options.taskLoaderRegistry;
    this.taskHandlerRegistry = options.taskHandlerRegistry;
    this.taskDispatchFactoryRegistry = options.taskDispatchFactoryRegistry ?? createDefaultTaskDispatchFactoryRegistry();
    this.checkRegistry = options.checkRegistry;
    this.getStageDefinition = options.getStageDefinition;
    this.worktreePath = options.worktreePath;
    this.enabledStages = options.enabledStages ? new Set(options.enabledStages) : undefined;
  }

  canHandle(stage: WorkflowStageId): boolean {
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
      message: GENERIC_STAGE_RUNNER_REQUIRES_WORK_MESSAGE,
    };
  }

  private appendTaskResult(ctx: StageContext, result: StageTaskResult): { decision: WorkflowDecision | null; result: StageTaskResult } {
    const execId = this.ensureStageExecution(ctx);
    let acceptedResult = result;
    let decision: WorkflowDecision | null = null;
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
          events: result.events,
          output: result.output,
          reason: result.reason,
          causedBy: result.causedBy,
        },
      });
      decision = completion?.decision ?? null;
      acceptedResult = this.acceptedTaskResult(completion?.run, ctx.issue.stage, result);
    }
    if (execId && ctx.stageExecutionRepo) {
      try {
        ctx.stageExecutionRepo.appendTaskResult(execId, acceptedResult);
      } catch (e) {
        log.warn('appendTaskResult failed', { error: e instanceof Error ? e.message : String(e) });
      }
    }
    return { decision, result: acceptedResult };
  }

  private async runRequestedTask(ctx: StageContext): Promise<StageRunResult> {
    const work = ctx.requestedWork;
    if (!work || work.kind !== 'task') {
      return { success: false, output: null, checkResults: [], message: 'Invalid requested work' };
    }
    const taskId = work.taskId;
    this.ensureStageExecution(ctx);

    if (typeof ctx.workflowApplicationService?.startTaskAttempt === 'function') {
      try {
        const executionId = `${ctx.issue.stage}-${ctx.issue.number}-${taskId}-${(ctx.requestedTask?.attempts ?? 0) + 1}`;
        ctx.workflowApplicationService.startTaskAttempt({
          issueId: ctx.issue.id,
          stage: ctx.issue.stage,
          taskId,
          evidence: { executionId },
        });
      } catch (e) {
        log.warn('startTaskAttempt failed', { taskId, error: e instanceof Error ? e.message : String(e) });
      }
    }

    let result: StageTaskResult | null;
    try {
      result = await this.executeTaskWork(ctx, taskId, {
        failedCheck: this.failedCheckForRequestedTask(ctx),
        attempt: (ctx.requestedTask?.attempts ?? 0) + 1,
      });
    } catch (err) {
      if (!ctx.signal?.aborted && ctx.workflowApplicationService) {
        try {
          ctx.workflowApplicationService.completeTask({
            issueId: ctx.issue.id,
            stage: ctx.issue.stage,
            taskId,
            result: { status: 'failed', reason: err instanceof Error ? err.message : String(err) },
          });
        } catch (e) {
          log.warn('completeTask after handler error failed', { taskId, error: e instanceof Error ? e.message : String(e) });
        }
      }
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
        message: `Task ${taskId} is not executable by generic stage runner`,
      };
    }

    const accepted = result.alreadyReported
      ? { decision: null as WorkflowDecision | null, result }
      : this.appendTaskResult(ctx, result);
    if (accepted.result.status === 'failed' || accepted.result.status === 'skipped') this.updateStageExecutionStatus(ctx, 'failed');

    return {
      success: accepted.result.status !== 'failed' && accepted.result.status !== 'skipped' && accepted.decision?.nextWork.kind !== 'failed',
      output: accepted.result.output ?? null,
      checkResults: [],
      message: accepted.result.status === 'failed' || accepted.result.status === 'skipped'
        ? accepted.result.reason ?? `Task ${taskId} failed`
        : accepted.decision?.nextWork.kind === 'failed'
          ? accepted.decision.nextWork.reason.message
          : undefined,
    };
  }

  private acceptedTaskResult(run: WorkflowRun | undefined, stage: WorkflowStageId, result: StageTaskResult): StageTaskResult {
    const task = run?.snapshot().stageRuns.find(candidate => candidate.stage === stage)?.tasks.find(candidate => candidate.id === result.taskId);
    if (!task) return result;
    return {
      ...result,
      status: task.status as Extract<TaskRunStatus, 'completed' | 'failed' | 'skipped'>,
      attempts: task.attempts,
      duration: task.duration,
      artifacts: task.artifacts,
      output: task.output ?? result.output,
      reason: task.reason ?? result.reason,
      causedBy: task.causedBy ?? result.causedBy,
    };
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
    if (typeof ctx.workflowApplicationService?.startCheckAttempt === 'function') {
      try {
        ctx.workflowApplicationService.startCheckAttempt({
          issueId: ctx.issue.id,
          stage: ctx.issue.stage,
          checkName,
          evidence: { executionId: `${ctx.issue.stage}-${ctx.issue.number}-${checkName}-check` },
        });
      } catch (e) {
        log.warn('startCheckAttempt failed', { checkName, error: e instanceof Error ? e.message : String(e) });
      }
    }
    const check = await resolveCheck(this.checkRegistry, checkCtx, checkName);
    const result = await check.run(checkCtx);
    const accepted = this.recordCheckResult(ctx, result);

    return {
      success: accepted.result.status === 'pass' || accepted.result.status === 'pending',
      output: accepted.result.output ?? null,
      checkResults: [accepted.result],
      message: accepted.result.status === 'pass' || accepted.result.status === 'pending'
        ? undefined
        : accepted.decision?.nextWork.kind === 'failed'
          ? accepted.decision.nextWork.reason.message ?? accepted.decision.nextWork.reason.reason
          : accepted.result.message ?? `Check "${checkName}" ${accepted.result.status}`,
    };
  }

  private recordCheckResult(ctx: StageContext, result: CheckResult): { decision: WorkflowDecision | null; result: CheckResult } {
    let persistedResult: PersistedCheckResult = {
      name: result.name,
      status: result.status,
      message: result.message,
      output: result.output,
    };

    const workflowUpdate = ctx.workflowApplicationService
      ? ctx.workflowApplicationService.recordCheckResult({
        issueId: ctx.issue.id,
        stage: ctx.issue.stage,
        result: persistedResult,
      })
      : null;
    persistedResult = this.acceptedCheckResult(workflowUpdate?.run, ctx.issue.stage, persistedResult);

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

    this.updateStageExecutionStatus(ctx, this.stageExecutionStatusAfterCheck(ctx, persistedResult, workflowUpdate?.decision));
    return { decision: workflowUpdate?.decision ?? null, result: persistedResult };
  }

  private acceptedCheckResult(run: WorkflowRun | undefined, stage: WorkflowStageId, result: CheckResult): CheckResult {
    const check = run?.snapshot().stageRuns.find(candidate => candidate.stage === stage)?.checks.find(candidate => candidate.name === result.name);
    if (!check) return result;
    return {
      ...result,
      status: this.toCheckResultStatus(check.status),
      message: check.message ?? result.message,
      output: check.output ?? result.output,
    };
  }

  private toCheckResultStatus(status: CheckRunStatus): CheckResult['status'] {
    if (status === 'passed') return 'pass';
    if (status === 'failed') return 'fail';
    if (status === 'running') return 'pending';
    return status;
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
    decision?: import('./model').WorkflowDecision,
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
    const policy = stageDefinition?.checkFailurePolicies?.find(candidate => candidate.checkName === checkName);
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

    const workSourceState = (stageRun as { workSourceState?: { evaluated?: boolean } }).workSourceState;
    if (!workSourceState?.evaluated) return true;

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
        if (ctx.workflowApplicationService) {
          ctx.workflowApplicationService.materializeTasks({
            issueId: ctx.issue.id,
            stage: ctx.issue.stage,
            tasks: [],
            workSourceState: 'missing',
          });
          materialized = true;
        }
        continue;
      }

      let tasks: Array<{ id: string; title: string; order: number; dependsOn: string[] }> = [];
      try {
        tasks = this.materializeLoadedTasks(ctx, workSource.kind, change);
      } catch {
        if (ctx.workflowApplicationService) {
          ctx.workflowApplicationService.materializeTasks({
            issueId: ctx.issue.id,
            stage: ctx.issue.stage,
            tasks: [],
            workSourceState: 'invalid',
          });
          materialized = true;
        }
        continue;
      }

      ctx.workflowApplicationService.materializeTasks({
        issueId: ctx.issue.id,
        stage: ctx.issue.stage,
        tasks,
        workSourceState: tasks.length === 0 ? 'empty' : undefined,
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

  private resolveExecutableTask(ctx: StageContext, taskId: string, stageDefinition: RuntimeStageDefinition): ExecutableTask | null {
    for (const workSource of stageDefinition.workSources ?? []) {
      if (workSource.kind === 'runtime') continue;

      if (workSource.kind === 'static') {
        const allowedTaskIds = new Set(workSource.taskIds ?? stageDefinition.tasks.map(task => task.id));
        const baseTaskId = this.baseRuntimeTaskId(taskId);
        if (!allowedTaskIds.has(taskId) && !allowedTaskIds.has(baseTaskId)) continue;
        const taskDef = this.taskLoaderRegistry.get('static')?.load(ctx).find(task => task.taskId === taskId || task.taskId === baseTaskId);
        if (!taskDef) continue;
        const policy = this.resolveTaskExecutionPolicy(stageDefinition, taskId, workSource.kind)
          ?? (baseTaskId !== taskId ? this.resolveTaskExecutionPolicy(stageDefinition, baseTaskId, workSource.kind) : undefined);
        return {
          ...taskDef,
          taskId,
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

    const runtimeTask = this.resolveRuntimeTask(ctx, stageDefinition, taskId);
    if (runtimeTask) return runtimeTask;

    return null;
  }

  private resolveTaskExecutionPolicy(
    stageDefinition: RuntimeStageDefinition,
    taskId: string,
    workSourceKind?: WorkSourceKind,
  ): TaskExecutionPolicy | undefined {
    return stageDefinition.taskExecutionPolicies?.find(policy => {
      const matchesTask = policy.taskId === taskId || policy.taskId === '*';
      const matchesWorkSource = workSourceKind === undefined || policy.workSourceKind === undefined || policy.workSourceKind === workSourceKind;
      return matchesTask && matchesWorkSource;
    });
  }

  private resolveRuntimeTask(ctx: StageContext, stageDefinition: RuntimeStageDefinition, taskId: string): ExecutableTask | null {
    const baseTaskId = this.baseRuntimeTaskId(taskId);
    const task = this.sourceTaskDefinition(stageDefinition, taskId)
      ?? this.runtimeTaskDefinition(ctx, taskId)
      ?? (baseTaskId !== taskId ? this.runtimeTaskDefinition(ctx, baseTaskId) : null);
    if (!task) return null;
    const policy = this.resolveTaskExecutionPolicy(stageDefinition, taskId, 'runtime')
      ?? (baseTaskId !== taskId ? this.resolveTaskExecutionPolicy(stageDefinition, baseTaskId, 'runtime') : undefined);
    const executionKind = policy?.kind ?? this.inferRuntimeTaskExecutionKind(task);
    return {
      taskId,
      title: task.title,
      kind: this.toHandlerKind(executionKind),
    };
  }

  private baseRuntimeTaskId(taskId: string): string {
    return taskId.replace(/:\d+$/, '');
  }

  private runtimeTaskDefinition(ctx: StageContext, taskId: string): TaskDefinition | null {
    const runtimeTask = ctx.workflowRun
      ?.stageRuns.find(stage => stage.stage === ctx.issue.stage)
      ?.tasks.find(task => {
        const projectedTask = task as typeof task & { id?: string; taskId?: string; causedByType?: string | null; causedBy?: unknown };
        const projectedTaskId = projectedTask.taskId ?? projectedTask.id;
        const hasRuntimeCause = projectedTask.causedByType !== undefined
          ? projectedTask.causedByType !== null
          : projectedTask.causedBy !== undefined;
        return projectedTaskId === taskId && hasRuntimeCause;
      });
    if (!runtimeTask) return null;
    return {
      id: taskId,
      title: runtimeTask.title,
      uses: inferWorkflowTaskUse(taskId),
      source: 'builtin',
    };
  }

  private inferRuntimeTaskExecutionKind(task: TaskDefinition): TaskExecutionKind {
    const uses = task.uses ?? inferWorkflowTaskUse(task.id);
    if (uses === 'mohist/rebase') return 'rebase-task';
    if (uses === 'mohist/openspec-sync' || uses === 'mohist/archive-change' || uses === 'mohist/merge' || uses === 'mohist/github-pr') return 'service-call';
    return 'agent-session';
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
      sourceTask: this.sourceTaskDefinition(stageDefinition, task.taskId),
    });
  }

  private sourceTaskDefinition(stageDefinition: RuntimeStageDefinition, taskId: string): import('./model').TaskDefinition | undefined {
    const baseTaskId = this.baseRuntimeTaskId(taskId);
    return stageDefinition.tasks.find(candidate => candidate.id === taskId || candidate.id === baseTaskId)
      ?? stageDefinition.checks
        .map(check => check.onFailure?.retry?.task)
        .find((task): task is import('./model').TaskDefinition => Boolean(task && (task.id === taskId || task.id === baseTaskId)));
  }

  private taskWorkSourceKind(ctx: StageContext, stageDefinition: RuntimeStageDefinition, taskId: string): WorkSourceKind | undefined {
    const baseTaskId = this.baseRuntimeTaskId(taskId);
    for (const workSource of stageDefinition.workSources ?? []) {
      if (workSource.kind === 'static') {
        const allowedTaskIds = new Set(workSource.taskIds ?? stageDefinition.tasks.map(task => task.id));
        if (allowedTaskIds.has(taskId) || allowedTaskIds.has(baseTaskId)) return workSource.kind;
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
