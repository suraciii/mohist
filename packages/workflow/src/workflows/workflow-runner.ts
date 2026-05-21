import type { WorkflowStageId } from '../model';
import { WorkflowRun } from '../model';
import type { WorkflowComponentRegistry } from './component-registry';
import type {
  WorkflowRunId,
  WorkflowRunResult,
  WorkflowRunner as WorkflowRunnerContract,
  WorkflowStore,
  WorkflowRunStatus,
  WorkflowStageState,
  WorkflowFailure,
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
    return this.workflowRun.stages;
  }

  get failure(): WorkflowFailure | null {
    return this.workflowRun.failure;
  }

  async start(): Promise<WorkflowRunResult> {
    if (this.workflowRun.status === 'pending') {
      this.workflowRun.start();
      await this.persist();
    }
    return this.run();
  }

  async resume(): Promise<WorkflowRunResult> {
    return this.run();
  }

  async run(): Promise<WorkflowRunResult> {
    if (this.workflowRun.status === 'pending') {
      this.workflowRun.start();
      await this.persist();
    }
    while (true) {
      const work = this.workflowRun.next();
      if (work.kind === 'complete') {
        const completed = this.workflowRun.passStage();
        await this.persist();
        if (!completed) break;
        continue;
      }
      if (work.kind === 'failed' || work.kind === 'blocked' || work.kind === 'await-approval') break;

      if (work.kind === 'task-source') {
        const continued = await this.createTasksFromSource(work.stage);
        await this.persist();
        if (!continued) break;
        continue;
      }

      if (work.kind === 'task') {
        const continued = await this.runTask(work.stage, work.taskId);
        await this.persist();
        if (!continued) break;
        continue;
      }

      const continued = await this.runCheck(work.stage, work.checkName);
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

  private async createTasksFromSource(stage: WorkflowStageId): Promise<boolean> {
    const source = this.workflowRun.taskSourceDefinition(stage);
    const definition = typeof source === 'string'
      ? { uses: source }
      : source;
    const component = this.registry.taskSource(definition?.uses);
    if (!component || !definition) {
      this.workflowRun.markTaskSourceMissing(stage);
      return false;
    }
    const result = await component.create({ run: this }).createTasks({
      run: this,
      stage,
      definition: {
        uses: definition.uses,
        with: definition.with ? { ...definition.with } : undefined,
      },
    });
    if (result.state === 'missing') {
      this.workflowRun.markTaskSourceMissing(stage);
    } else if (result.state === 'invalid') {
      this.workflowRun.markTaskSourceInvalid(stage);
    } else if (result.state === 'empty') {
      this.workflowRun.markTaskSourceEmpty(stage);
    } else {
      this.workflowRun.addTasksFromSource(stage, result.tasks);
    }
    return result.tasks.length > 0;
  }

  private async runTask(stage: WorkflowStageId, taskId: string): Promise<boolean> {
    const definition = this.workflowRun.taskDefinition(stage, taskId);
    if (!definition) return false;
    const component = this.registry.task(definition.uses);
    if (!component) return false;
    const result = await component.create({ run: this }).run({
      run: this,
      stage,
      taskId: definition.id,
      definition: {
        id: definition.id,
        title: definition.title,
        uses: definition.uses,
        with: definition.with ? { ...definition.with } : undefined,
      },
    });
    if (result.status === 'completed') {
      this.workflowRun.completeTask(stage, definition.id);
    } else {
      this.workflowRun.failTask(stage, definition.id, result);
    }
    return result.status === 'completed';
  }

  private async runCheck(stage: WorkflowStageId, checkName: string): Promise<boolean> {
    const definition = this.workflowRun.checkDefinition(stage, checkName);
    if (!definition) return false;
    const component = this.registry.check(definition.uses);
    if (!component) return false;
    const result = await component.create({ run: this }).run({
      run: this,
      stage,
      checkName: definition.name,
      definition,
    });
    if (result.status === 'pass') {
      this.workflowRun.passCheck(stage, definition.name, result);
    } else if (result.status === 'pending') {
      this.workflowRun.resetCheck(stage, definition.name, result);
    } else {
      this.workflowRun.failCheck(stage, definition.name, result);
    }
    return result.status === 'pass';
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
