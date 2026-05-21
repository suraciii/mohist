import type { StageCompletionGuard, WorkflowStageId } from '../model';
import { WorkflowRun } from '../model';
import type { WorkflowComponentRegistry } from './component-registry';
import {
  checkDefinition,
  taskDefinition,
  taskSourceDefinition,
} from './definition-context';
import { stateFromRun } from './workflow-state';
import type {
  Workflow,
  WorkflowCheckResult,
  WorkflowRunId,
  WorkflowRunResult,
  WorkflowStore,
  WorkflowTaskResult,
  WorkflowTaskSourceResult,
  WorkflowState,
} from './types';

type NextExecution =
  | { type: 'task'; stage: WorkflowStageId; taskId: string }
  | { type: 'check'; stage: WorkflowStageId; checkName: string }
  | { type: 'task-source'; stage: WorkflowStageId }
  | { type: 'terminal'; result: Omit<WorkflowRunResult, 'state'> };

export class RunnableWorkflow implements Workflow {
  constructor(
    private readonly run: WorkflowRun,
    private readonly store: WorkflowStore,
    private readonly registry: WorkflowComponentRegistry,
    private readonly maxSteps = 1000,
  ) {}

  get id(): WorkflowRunId {
    return this.run.id;
  }

  get state(): WorkflowState {
    return stateFromRun(this.run);
  }

  async start(): Promise<WorkflowRunResult> {
    await this.persist();
    return this.resume();
  }

  async resume(): Promise<WorkflowRunResult> {
    let steps = 0;
    while (steps++ < this.maxSteps) {
      const next = selectNextExecution(this.run);
      if (next.type === 'terminal') return this.result(next.result);

      if (next.type === 'task-source') {
        const result = await this.runTaskSource(next.stage);
        this.run.materializeTasks(next.stage, result.tasks, result.state);
        await this.persist();
        continue;
      }

      if (next.type === 'task') {
        const result = await this.runTask(next.stage, next.taskId);
        this.run.completeTask(next.stage, next.taskId, result);
        await this.persist();
        continue;
      }

      const result = await this.runCheck(next.stage, next.checkName);
      this.run.recordCheckResult(next.stage, {
        ...result,
        name: result.name ?? next.checkName,
      });
      await this.persist();
    }

    return this.result({
      status: 'stopped',
      stage: this.run.currentStage,
      message: `Workflow stopped after ${this.maxSteps} steps`,
    });
  }

  async pause(reason?: string): Promise<WorkflowRunResult> {
    this.run.interruptRunningWorkAttempts(reason ?? 'workflow-paused');
    await this.persist();
    return this.result({
      status: 'stopped',
      stage: this.run.currentStage,
      message: reason ?? 'Workflow paused',
    });
  }

  async approve(): Promise<WorkflowRunResult> {
    this.run.approveStage(this.run.currentStage);
    await this.persist();
    return this.resume();
  }

  async reject(reason?: string): Promise<WorkflowRunResult> {
    this.run.rejectStage(this.run.currentStage, { output: reason });
    await this.persist();
    return this.result(statusResultFromRun(this.run));
  }

  async persist(): Promise<void> {
    await this.store.save(this.state);
  }

  private async runTaskSource(stage: WorkflowStageId): Promise<WorkflowTaskSourceResult> {
    const sourceDefinition = taskSourceDefinition(this.run.workflowDefinitionSnapshot, stage);
    const component = this.registry.taskSource(sourceDefinition?.uses);
    if (!component || !sourceDefinition) {
      return { tasks: [], state: 'missing' };
    }
    try {
      return await component.create({ state: this.state }).run({
        state: this.state,
        stage,
        definition: sourceDefinition,
      });
    } catch {
      return { tasks: [], state: 'invalid' };
    }
  }

  private async runTask(stage: WorkflowStageId, taskId: string): Promise<WorkflowTaskResult> {
    const definition = taskDefinition(this.run.workflowDefinitionSnapshot, stage, taskId);
    const component = this.registry.task(definition?.uses);
    if (!component || !definition) {
      return {
        status: 'failed',
        reason: `No task registered for ${definition?.uses ?? taskId}`,
      };
    }
    try {
      return await component.create({ state: this.state }).run({
        state: this.state,
        stage,
        taskId,
        definition,
      });
    } catch (error) {
      return {
        status: 'failed',
        reason: errorMessage(error),
      };
    }
  }

  private async runCheck(stage: WorkflowStageId, checkName: string): Promise<WorkflowCheckResult> {
    const definition = checkDefinition(this.run.workflowDefinitionSnapshot, stage, checkName);
    const component = this.registry.check(definition?.uses);
    if (!component || !definition) {
      return {
        name: checkName,
        status: 'error',
        message: `No check registered for ${definition?.uses ?? checkName}`,
      };
    }
    try {
      return await component.create({ state: this.state }).run({
        state: this.state,
        stage,
        checkName,
        definition,
      });
    } catch (error) {
      return {
        name: checkName,
        status: 'error',
        message: errorMessage(error),
      };
    }
  }

  private result(result: Omit<WorkflowRunResult, 'state'>): WorkflowRunResult {
    return { ...result, state: this.state };
  }
}

function selectNextExecution(run: WorkflowRun): NextExecution {
  const work = run.nextWork();
  if (work.kind === 'complete') {
    return { type: 'terminal', result: { status: 'completed', stage: run.currentStage, message: 'Workflow completed' } };
  }
  if (work.kind === 'failed') {
    return {
      type: 'terminal',
      result: {
        status: 'failed',
        stage: work.reason.stage,
        message: work.reason.message ?? work.reason.reason,
      },
    };
  }
  if (work.kind === 'await-approval') {
    return {
      type: 'terminal',
      result: { status: 'awaiting-approval', stage: work.stage, message: `Awaiting ${work.stage} approval` },
    };
  }
  if (work.kind === 'blocked') {
    if (!work.reason.complete && work.reason.reason === 'dynamic-source-not-evaluated') {
      return { type: 'task-source', stage: work.stage };
    }
    return {
      type: 'terminal',
      result: { status: 'blocked', stage: work.stage, message: blockedReasonMessage(work.reason) },
    };
  }
  if (work.kind === 'task') return { type: 'task', stage: work.stage, taskId: work.taskId };
  return { type: 'check', stage: work.stage, checkName: work.checkName };
}

function statusResultFromRun(run: WorkflowRun): Omit<WorkflowRunResult, 'state'> {
  const work = run.nextWork();
  const next = selectNextExecution(run);
  return next.type === 'terminal'
    ? next.result
    : { status: 'running', stage: 'stage' in work ? work.stage : run.currentStage };
}

function blockedReasonMessage(reason: StageCompletionGuard): string {
  if (reason.complete) return 'Workflow is blocked';
  if ('taskId' in reason) return `${reason.reason}: ${reason.taskId}`;
  if ('checkName' in reason) return `${reason.reason}: ${reason.checkName}`;
  if ('stage' in reason) return `${reason.reason}: ${reason.stage}`;
  return 'Workflow is blocked';
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
