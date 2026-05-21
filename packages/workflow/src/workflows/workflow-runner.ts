import type { WorkflowStageId } from '../model';
import { WorkflowRun } from '../model';
import type { WorkflowComponentRegistry } from './component-registry';
import { CheckRunner } from './check-runner';
import { TaskRunner } from './task-runner';
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
  ) {
    this.checkRunner = new CheckRunner(registry);
    this.taskRunner = new TaskRunner(registry);
  }

  private readonly checkRunner: CheckRunner;
  private readonly taskRunner: TaskRunner;

  get id(): WorkflowRunId {
    return this.workflowRun.id;
  }

  get status(): WorkflowRunStatus {
    const status = this.workflowRun.status;
    return status === 'passed' ? 'completed' : status;
  }

  get currentStage(): WorkflowStageId {
    return this.workflowRun.currentStage.stage;
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
        const continued = await this.initTasks(work);
        await this.persist();
        if (!continued) break;
        continue;
      }

      if (work.kind === 'task') {
        const continued = await this.runTask(work);
        await this.persist();
        if (!continued) break;
        continue;
      }

      const continued = await this.runCheck(work);
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
      stage: this.workflowRun.currentStage.stage,
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
      stage: this.workflowRun.currentStage.stage,
      message: reason,
    });
  }

  async persist(): Promise<void> {
    await this.store.save(this.workflowRun);
  }

  private async initTasks(work: Extract<ReturnType<WorkflowRun['next']>, { kind: 'task-source' }>): Promise<boolean> {
    const component = this.registry.taskSource(work.definition.uses);
    if (!component) {
      this.workflowRun.markTaskSourceMissing();
      return false;
    }
    const result = await component.create({ run: this }).createTasks({
      run: this,
      stage: work.stage,
      definition: {
        uses: work.definition.uses,
        with: work.definition.with ? { ...work.definition.with } : undefined,
      },
    });
    if (result.state === 'missing') {
      this.workflowRun.markTaskSourceMissing();
    } else if (result.state === 'invalid') {
      this.workflowRun.markTaskSourceInvalid();
    } else if (result.state === 'empty') {
      this.workflowRun.markTaskSourceEmpty();
    } else {
      this.workflowRun.addTasks(result.tasks);
    }
    return result.tasks.length > 0;
  }

  private async runTask(work: Extract<ReturnType<WorkflowRun['next']>, { kind: 'task' }>): Promise<boolean> {
    const result = await this.taskRunner.run(work);
    if (!result) return false;
    if (result.status === 'completed') {
      this.workflowRun.completeTask();
      return true;
    }
    this.workflowRun.failTask(result);
    return false;
  }

  private async runCheck(work: Extract<ReturnType<WorkflowRun['next']>, { kind: 'check' }>): Promise<boolean> {
    const result = await this.checkRunner.run(work);
    if (!result) return false;
    if (result.status === 'pass') {
      this.workflowRun.passCheck(result);
    } else if (result.status === 'pending') {
      this.workflowRun.resetCheck(result);
    } else {
      this.workflowRun.failCheck(result);
    }
    return result.status === 'pass';
  }

  private result(result: WorkflowRunResult): WorkflowRunResult {
    return result;
  }

  private resultFromRun(): WorkflowRunResult {
    if (this.workflowRun.status === 'passed') return { status: 'completed', stage: this.workflowRun.currentStage.stage };
    if (this.workflowRun.status === 'failed') {
      return {
        status: 'failed',
        stage: this.workflowRun.currentStage.stage,
        message: this.workflowRun.failure?.message ?? this.workflowRun.failure?.reason,
      };
    }
    if (this.workflowRun.status === 'cancelled') return { status: 'stopped', stage: this.workflowRun.currentStage.stage };
    return { status: 'running', stage: this.workflowRun.currentStage.stage };
  }
}
