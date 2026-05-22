import type { WorkflowStageId } from '../domain';
import { WorkflowRun } from '../domain';
import type { HandlerRegistry } from './registry';
import type {
  WorkflowRunId,
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
    private readonly registry: HandlerRegistry,
  ) {}

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
    await this.executeLoop();
  }

  async resume(): Promise<void> {
    await this.executeLoop();
  }

  private async executeLoop(): Promise<void> {
    if (this.workflowRun.status === 'pending' || this.workflowRun.status === 'paused') {
      this.workflowRun.start();
      await this.save();
    }
    while (true) {
      const work = this.workflowRun.next();
      if (work.kind === 'complete') {
        await this.save();
        break;
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
    await this.executeLoop();
  }

  async reject(reason?: string): Promise<void> {
    this.workflowRun.reject({ output: reason });
    await this.save();
  }

  private async save(): Promise<void> {
    await this.store.save(this.workflowRun);
  }

  private async initTasks(work: Extract<ReturnType<WorkflowRun['next']>, { kind: 'stage-init' }>): Promise<boolean> {
    if (!work.definition.tasksFrom) {
      this.workflowRun.initTasks();
      return true;
    }

    const loader = this.registry.taskLoader(work.definition.tasksFrom.uses);
    if (!loader) {
      this.workflowRun.failStage(`Task loader ${work.definition.tasksFrom.uses} is not registered`);
      return false;
    }
    const result = await loader.load({
      run: this,
      stage: work.stage,
      definition: {
        uses: work.definition.tasksFrom.uses,
        with: work.definition.tasksFrom.with,
      },
    });
    if (result.state === 'missing' || result.state === 'invalid') {
      this.workflowRun.failStage(result.message ?? `Task loader ${work.definition.tasksFrom.uses}: ${result.state}`);
    } else if (result.state === 'loaded') {
      this.workflowRun.initTasks(result.tasks);
    } else {
      this.workflowRun.initTasks();
    }
    return true;
  }

  private async runTask(work: Extract<ReturnType<WorkflowRun['next']>, { kind: 'task' }>): Promise<boolean> {
    const handler = this.registry.task(work.task.uses);
    if (!handler) return false;
    const result = await handler.run({
      id: work.task.id,
      title: work.task.title,
      with: work.task.with,
    });
    if (result.status === 'completed') {
      this.workflowRun.completeTask();
      return true;
    }
    this.workflowRun.failTask(result);
    return false;
  }

  private async runCheck(work: Extract<ReturnType<WorkflowRun['next']>, { kind: 'check' }>): Promise<boolean> {
    const handler = this.registry.check(work.check.uses);
    if (!handler) return false;
    const result = await handler.run({
      name: work.check.name,
      title: work.check.title,
      with: work.check.with,
    });
    const checkResult = { ...result, name: work.check.name };
    if (result.status === 'pass') {
      this.workflowRun.passCheck(checkResult);
    } else if (result.status === 'pending') {
      this.workflowRun.pendingCheck(checkResult);
    } else {
      this.workflowRun.failCheck(checkResult);
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
