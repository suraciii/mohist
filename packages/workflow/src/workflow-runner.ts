import type { WorkflowStageId } from './model';
import { WorkflowRun } from './model';
import type { WorkflowComponentRegistry } from './component-registry';
import { CheckRunner } from './check-runner';
import { TaskRunner } from './task-runner';
import type {
  WorkflowRunId,
  WorkflowRunner as WorkflowRunnerContract,
  WorkflowStore,
  WorkflowRunStatus,
  WorkflowStageState,
  WorkflowFailure,
} from './workflow-types';

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

  async start(): Promise<void> {
    if (this.workflowRun.status === 'pending') {
      this.workflowRun.start();
      await this.save();
    }
    await this.run();
  }

  async resume(): Promise<void> {
    await this.run();
  }

  async run(): Promise<void> {
    if (this.workflowRun.status === 'pending' || this.workflowRun.status === 'paused') {
      this.workflowRun.start();
      await this.save();
    }
    while (true) {
      const work = this.workflowRun.next();
      if (work.kind === 'complete') {
        const completed = this.workflowRun.passStage();
        await this.save();
        if (await this.pauseIfRequested()) break;
        if (!completed) break;
        continue;
      }
      if (work.kind === 'failed' || work.kind === 'blocked' || work.kind === 'await-approval') break;

      if (work.kind === 'stage-init') {
        const continued = await this.initTasks(work);
        await this.save();
        if (await this.pauseIfRequested()) break;
        if (!continued) break;
        continue;
      }

      if (work.kind === 'task') {
        const continued = await this.runTask(work);
        await this.save();
        if (await this.pauseIfRequested()) break;
        if (!continued) break;
        continue;
      }

      const continued = await this.runCheck(work);
      await this.save();
      if (await this.pauseIfRequested()) break;
      if (!continued) break;
    }

    await this.save();
  }

  async pause(_reason?: string): Promise<void> {
    this.workflowRun.requestPause();
    await this.save();
  }

  async approve(): Promise<void> {
    this.workflowRun.approve();
    await this.save();
    await this.run();
  }

  async reject(reason?: string): Promise<void> {
    this.workflowRun.reject({ output: reason });
    await this.save();
  }

  async save(): Promise<void> {
    await this.store.save(this.workflowRun);
  }

  private async initTasks(work: Extract<ReturnType<WorkflowRun['next']>, { kind: 'stage-init' }>): Promise<boolean> {
    if (!work.definition.tasksFrom) {
      this.workflowRun.initTasks();
      return true;
    }

    const component = this.registry.taskSource(work.definition.tasksFrom.uses);
    if (!component) {
      this.workflowRun.failStage(`Task source ${work.definition.tasksFrom.uses} is not registered`);
      return false;
    }
    const result = await component.create({ run: this }).createTasks({
      run: this,
      stage: work.stage,
      definition: {
        uses: work.definition.tasksFrom.uses,
        with: work.definition.tasksFrom.with ? { ...work.definition.tasksFrom.with } : undefined,
      },
    });
    if (result.state === 'missing') {
      this.workflowRun.failStage(`Task source ${work.definition.tasksFrom.uses} is missing`);
    } else if (result.state === 'invalid') {
      this.workflowRun.failStage(`Task source ${work.definition.tasksFrom.uses} is invalid`);
    } else if (result.state === 'empty') {
      this.workflowRun.initTasks();
    } else {
      this.workflowRun.initTasks(result.tasks);
    }
    return true;
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
      this.workflowRun.pendingCheck(result);
    } else {
      this.workflowRun.failCheck(result);
    }
    return result.status === 'pass';
  }

  private async pauseIfRequested(): Promise<boolean> {
    if (!this.workflowRun.pauseRequested) return false;
    this.workflowRun.pause();
    await this.save();
    return true;
  }

}
