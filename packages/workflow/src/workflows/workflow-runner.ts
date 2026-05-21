import type { StageRun, WorkflowStageId } from '../model';
import { WorkflowRun } from '../model';
import type { WorkflowComponentRegistry } from './component-registry';
import type {
  WorkflowCheckDefinitionContext,
  WorkflowRunId,
  WorkflowRunResult,
  WorkflowRunner as WorkflowRunnerContract,
  WorkflowStore,
  WorkflowRunStatus,
  WorkflowStageState,
  WorkflowFailure,
  WorkflowTaskDefinitionContext,
} from './types';

export class WorkflowRunner implements WorkflowRunnerContract {
  constructor(
    private readonly workflowRun: WorkflowRun,
    private readonly store: WorkflowStore,
    private readonly registry: WorkflowComponentRegistry,
  ) {}

  get id(): WorkflowRunId {
    return this.workflowRun.id;
  }

  get status(): WorkflowRunStatus {
    const status = this.workflowRun.status;
    return status === 'passed' ? 'completed' : status;
  }

  get currentStage(): WorkflowStageId {
    return this.workflowRun.currentStage;
  }

  get stages(): WorkflowStageState[] {
    return this.workflowRun.stageRuns.map(stageRun => ({
      stage: stageRun.stage,
      status: stageRun.status,
      order: stageRun.order,
      attemptSequence: stageRun.attemptSequence,
      tasks: stageRun.tasks.map((taskRun, index) => {
        const task = stageRun.definition.tasks[index];
        return {
          id: task?.id ?? `task-${index}`,
          title: task?.title ?? `Task ${index + 1}`,
          uses: task?.uses,
          status: taskRun.status,
        };
      }),
      checks: stageRun.checks.map(check => ({
        name: check.name,
        title: check.title,
        status: check.status,
        message: check.message,
        output: check.output,
      })),
      approval: stageRun.approval ? { ...stageRun.approval } : null,
      failure: stageRun.failure,
      commitPoint: stageRun.commitPoint,
      workSourceState: stageRun.workSourceState,
    }));
  }

  get failure(): WorkflowFailure | null {
    return this.workflowRun.failure;
  }

  async start(): Promise<WorkflowRunResult> {
    if (this.workflowRun.status === 'pending') {
      this.workflowRun.start();
      await this.persist();
    }
    return this.resume();
  }

  async resume(): Promise<WorkflowRunResult> {
    return this.runUntilBlocked();
  }

  async run(): Promise<WorkflowRunResult> {
    if (this.workflowRun.status === 'pending') {
      this.workflowRun.start();
      await this.persist();
    }
    return this.runUntilBlocked();
  }

  private async runUntilBlocked(): Promise<WorkflowRunResult> {
    while (true) {
      const work = this.workflowRun.next();
      if (work.kind === 'complete') {
        const stageRun = this.currentStageRun();
        if (!stageRun) break;
        const completed = this.completeCurrentStage(stageRun);
        await this.persist();
        if (!completed) break;
        continue;
      }
      if (work.kind === 'failed' || work.kind === 'blocked' || work.kind === 'await-approval') break;

      const stageRun = this.stageRun(work.stage);
      if (!stageRun) break;

      if (work.kind === 'task-source') {
        const continued = await this.runTaskSource(stageRun);
        await this.persist();
        if (!continued) break;
        continue;
      }

      if (work.kind === 'task') {
        const taskDefinition = stageRun.definition.tasks.find(candidate => candidate.id === work.taskId);
        if (!taskDefinition) break;
        const continued = await this.runTask(stageRun, taskDefinition);
        await this.persist();
        if (!continued) break;
        continue;
      }

      const checkDefinition = stageRun.definition.checks.find(definition => definition.name === work.checkName);
      if (!checkDefinition) break;
      const continued = await this.runCheck(stageRun, checkDefinition);
      await this.persist();
      if (!continued) break;
    }

    await this.persist();
    return this.resultFromRun();
  }

  async pause(reason?: string): Promise<WorkflowRunResult> {
    await this.persist();
    return this.result({
      status: 'stopped',
      stage: this.workflowRun.currentStage,
      message: reason ?? 'Workflow paused',
    });
  }

  async approve(): Promise<WorkflowRunResult> {
    await this.persist();
    return this.resultFromRun();
  }

  async reject(reason?: string): Promise<WorkflowRunResult> {
    await this.persist();
    return this.result({
      status: 'stopped',
      stage: this.workflowRun.currentStage,
      message: reason,
    });
  }

  async persist(): Promise<void> {
    await this.store.save(this.workflowRun);
  }

  private currentStageRun(): StageRun | null {
    return this.workflowRun.stageRuns.find(candidate => candidate.stage === this.workflowRun.currentStage) ?? null;
  }

  private stageRun(stage: WorkflowStageId): StageRun | null {
    return this.workflowRun.stageRuns.find(candidate => candidate.stage === stage) ?? null;
  }

  private async runTaskSource(stageRun: StageRun): Promise<boolean> {
    const source = stageRun.definition.tasksFrom;
    const definition = typeof source === 'string'
      ? { uses: source }
      : source;
    const component = this.registry.taskSource(definition?.uses);
    if (!component || !definition) {
      stageRun.workSourceState = { evaluated: true, missing: true };
      return false;
    }
    const result = await component.create({ run: this }).run({
      run: this,
      stage: stageRun.stage,
      definition: {
        uses: definition.uses,
        with: definition.with ? { ...definition.with } : undefined,
      },
    });
    if (result.state === 'missing') {
      stageRun.workSourceState = { evaluated: true, missing: true };
    } else if (result.state === 'invalid') {
      stageRun.workSourceState = { evaluated: true, invalid: true };
    } else if (result.state === 'empty') {
      stageRun.workSourceState = { evaluated: true, empty: true };
    } else if (result.tasks.length === 0) {
      stageRun.workSourceState = { evaluated: true, empty: true };
    } else {
      stageRun.workSourceState = { evaluated: true, tasks: result.tasks };
    }
    for (const task of result.tasks) {
      stageRun.addTask(task.id, task.title, task.uses);
    }
    return result.tasks.length > 0;
  }

  private async runTask(stageRun: StageRun, definition: WorkflowTaskDefinitionContext): Promise<boolean> {
    const component = this.registry.task(definition.uses);
    if (!component) return false;
    stageRun.startTask();
    const result = await component.create({ run: this }).run({
      run: this,
      stage: stageRun.stage,
      taskId: definition.id,
      definition: {
        id: definition.id,
        title: definition.title,
        uses: definition.uses,
        with: definition.with ? { ...definition.with } : undefined,
      },
    });
    if (result.status === 'completed') {
      stageRun.completeTask();
      return true;
    }
    stageRun.failTask();
    const failure = {
      reason: 'task-failed' as const,
      stage: stageRun.stage,
      taskId: definition.id,
      message: result.reason,
      causedBy: result.causedBy,
    };
    stageRun.status = 'failed';
    stageRun.failure = failure;
    this.workflowRun.status = 'failed';
    this.workflowRun.failure = failure;
    return false;
  }

  private async runCheck(stageRun: StageRun, definition: WorkflowCheckDefinitionContext): Promise<boolean> {
    const component = this.registry.check(definition.uses);
    if (!component) return false;
    const result = await component.create({ run: this }).run({
      run: this,
      stage: stageRun.stage,
      checkName: definition.name,
      definition,
    });
    const check = stageRun.checks.find(candidate => candidate.name === definition.name);
    if (!check) return false;
    check.message = result.message ?? null;
    check.output = result.output ?? null;
    if (result.status === 'pass') {
      check.pass();
      return true;
    }
    if (result.status === 'pending') {
      check.reset();
      return false;
    }
    check.fail();
    const failure = {
      reason: 'check-unrepaired' as const,
      stage: stageRun.stage,
      checkName: definition.name,
      message: result.message,
    };
    stageRun.status = 'failed';
    stageRun.failure = failure;
    this.workflowRun.status = 'failed';
    this.workflowRun.failure = failure;
    return false;
  }

  private completeCurrentStage(stageRun: StageRun): boolean {
    if (stageRun.tasks.some(task => task.status !== 'completed')) return false;
    if (stageRun.checks.some(check => check.status !== 'passed')) return false;
    stageRun.status = 'passed';
    const next = this.workflowRun.stageRuns[stageRun.order + 1];
    if (!next) {
      this.workflowRun.status = 'passed';
      return true;
    }
    this.workflowRun.currentStage = next.stage;
    next.start();
    return true;
  }

  private result(result: WorkflowRunResult): WorkflowRunResult {
    return result;
  }

  private resultFromRun(): WorkflowRunResult {
    if (this.workflowRun.status === 'passed') return { status: 'completed', stage: this.workflowRun.currentStage };
    if (this.workflowRun.status === 'failed') {
      return {
        status: 'failed',
        stage: this.workflowRun.currentStage,
        message: this.workflowRun.failure?.message ?? this.workflowRun.failure?.reason,
      };
    }
    if (this.workflowRun.status === 'cancelled') return { status: 'stopped', stage: this.workflowRun.currentStage };
    return { status: 'running', stage: this.workflowRun.currentStage };
  }
}
