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

class Mutex {
  private queue: Promise<void> = Promise.resolve();

  run<T>(fn: () => Promise<T>): Promise<T> {
    const prev = this.queue;
    let release!: () => void;
    this.queue = new Promise(r => { release = r; });
    return prev.then(() => fn().finally(release));
  }

  idle(): Promise<void> {
    return this.queue;
  }
}

class LoopController {
  private loop: Promise<void> | null = null;
  private wakeResolve: (() => void) | null = null;
  private idlePromise: Promise<void> | null = null;
  private idleResolve: (() => void) | null = null;

  start(run: () => Promise<void>): void {
    if (this.loop) return;
    this.markActive();
    const loop = run().finally(() => {
      if (this.loop === loop) {
        this.loop = null;
        this.wakeResolve = null;
        this.markIdle();
      }
    });
    this.loop = loop;
  }

  wake(): void {
    const resolve = this.wakeResolve;
    this.wakeResolve = null;
    if (resolve) {
      this.markActive();
      resolve();
    }
  }

  async wait(): Promise<void> {
    this.markIdle();
    await new Promise<void>(resolve => {
      this.wakeResolve = resolve;
    });
  }

  async nextYield(): Promise<void> {
    await this.idlePromise;
  }

  private markActive(): void {
    if (this.idlePromise) return;
    this.idlePromise = new Promise(resolve => {
      this.idleResolve = resolve;
    });
  }

  private markIdle(): void {
    this.idleResolve?.();
    this.idlePromise = null;
    this.idleResolve = null;
  }
}

export class WorkflowRunner implements WorkflowRunnerContract {
  private signal?: AbortSignal;
  private readonly lock = new Mutex();
  private readonly loop = new LoopController();

  constructor(
    private readonly workflowRun: WorkflowRun,
    private readonly store: WorkflowStore,
    private readonly registry: HandlerRegistry,
    private readonly definitionStages: import('../domain/workflow-definition').StageDefinition[] = [],
  ) {}

  withSignal(signal: AbortSignal): this {
    this.signal = signal;
    return this;
  }

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
    await this.lock.run(async () => {
      if (this.workflowRun.status === 'pending') {
        this.workflowRun.start();
        await this.save();
      }
    });
    this.ensureLoop();
  }

  async run(): Promise<void> {
    await this.lock.run(async () => {
      if (this.workflowRun.status === 'pending') {
        this.workflowRun.start();
        await this.save();
      }
    });
    await this.runLoop();
  }

  async nextYield(): Promise<void> {
    await this.loop.nextYield();
  }

  async resume(): Promise<void> {
    await this.lock.run(async () => {
      if (this.workflowRun.status === 'paused') {
        this.workflowRun.start();
        await this.save();
      }
    });
    this.wake();
  }

  private async runLoop(): Promise<void> {
    this.ensureLoop();
    await this.nextYield();
  }

  private ensureLoop(): void {
    this.loop.start(() => this.executeLoop());
  }

  private wake(): void {
    this.loop.wake();
    this.ensureLoop();
  }

  private async waitForWake(): Promise<void> {
    await this.loop.wait();
  }

  private async executeLoop(): Promise<void> {
    while (true) {
      if (this.signal?.aborted) {
        await this.lock.run(async () => {
          this.workflowRun.requestPause();
          this.workflowRun.pause();
          await this.save();
        });
        break;
      }

      const work = this.workflowRun.next();
      if (work.kind === 'complete') {
        await this.lock.run(() => this.save());
        break;
      }
      if (work.kind === 'failed' || work.kind === 'blocked' || work.kind === 'await-approval') {
        await this.lock.run(() => this.save());
        await this.waitForWake();
        continue;
      }

      if (work.kind === 'stage-init') {
        const continued = await this.initTasks(work);
        await this.lock.run(() => this.save());
        if (await this.pauseIfRequested()) break;
        if (!continued) break;
        continue;
      }

      if (work.kind === 'task') {
        const continued = await this.runTask(work);
        await this.lock.run(() => this.save());
        if (await this.pauseIfRequested()) break;
        if (!continued) break;
        continue;
      }

      const continued = await this.runCheck(work);
      await this.lock.run(() => this.save());
      if (await this.pauseIfRequested()) break;
      if (!continued) break;
    }
  }

  async pause(_reason?: string): Promise<void> {
    await this.lock.run(async () => {
      this.workflowRun.requestPause();
      await this.save();
    });
  }

  async approve(): Promise<void> {
    await this.lock.run(async () => {
      this.workflowRun.approve();
      await this.save();
    });
    this.wake();
  }

  async reject(reason?: string): Promise<void> {
    await this.lock.run(async () => {
      this.workflowRun.reject({ output: reason });
      await this.save();
    });
  }

  async retry(): Promise<void> {
    await this.lock.run(async () => {
      this.workflowRun.retry();
      await this.save();
    });
    this.wake();
  }

  async rerun(): Promise<void> {
    await this.lock.run(async () => {
      this.workflowRun.rerun();
      await this.save();
    });
    this.wake();
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
      const retryInjected = this.tryInjectRetryTask(work.stage, work.check.name, result);
      if (retryInjected) {
        this.workflowRun.resetCheck(checkResult);
        this.workflowRun.clearStageFailure();
      } else {
        this.workflowRun.failCheck(checkResult);
      }
    }
    return result.status === 'pass';
  }

  private tryInjectRetryTask(stage: string, checkName: string, result: import('../domain/run/types').CheckResult): boolean {
    const stageDef = this.definitionStages.find(def => def.stage === stage);
    if (!stageDef) return false;
    const checkDef = stageDef.checks.find(c => c.name === checkName);
    if (!checkDef?.onFailure?.retry) return false;
    const { limit, task } = checkDef.onFailure.retry;
    const retryCount = this.workflowRun.retryCountForCheck(checkName);
    if (retryCount >= limit) return false;
    this.workflowRun.injectRetryTask(checkName, {
      id: `${task.id}:${retryCount + 1}`,
      title: task.title,
      uses: task.uses,
      with: { ...task.with, failedCheckResult: result },
    });
    return true;
  }

  private async pauseIfRequested(): Promise<boolean> {
    if (!this.workflowRun.pauseRequested) return false;
    this.workflowRun.pause();
    await this.save();
    return true;
  }
}
