import type { WorkflowTasksFromSource } from '../workflow-definition';
import { WorkflowDomainError } from '../errors';
import { StageCheck } from './stage-check';
import { TaskRun } from './task-run';
import type { ApprovalInput, FailureDetails, LoadedTaskInput, StageRunStatus, StageWork } from './types';

export class StageRun {
  readonly tasks: TaskRun[] = [];
  readonly checks: StageCheck[] = [];
  failure: FailureDetails | null = null;
  approval: { status: 'awaiting' | 'approved' | 'rejected'; output: unknown | null; requestedAt: string; respondedAt: string | null } | null = null;

  private _started = false;
  private _initialized = false;

  constructor(
    readonly stage: string,
    readonly order: number,
    private readonly staticTasks: { id: string; title: string; uses?: string; with?: Record<string, unknown> }[],
    private readonly staticChecks: { name: string; title: string; uses?: string; with?: Record<string, unknown> }[],
    readonly tasksFrom?: WorkflowTasksFromSource,
    readonly requiresApproval?: boolean,
  ) {}

  get status(): StageRunStatus {
    if (this.failure) return 'failed';
    if (!this._started) return 'pending';
    if (this.approval?.status === 'awaiting') return 'awaiting-approval';
    if (this.isComplete) {
      if (this.requiresApproval && this.approval?.status !== 'approved') return 'running';
      return 'passed';
    }
    return 'running';
  }

  get isComplete(): boolean {
    return this._initialized
      && this.tasks.every(t => t.status === 'completed')
      && this.checks.every(c => c.status === 'passed');
  }

  get initialized(): boolean {
    return this._initialized;
  }

  get currentTask(): TaskRun | null {
    return this.tasks.find(task => task.status !== 'completed' && task.status !== 'failed') ?? null;
  }

  start(): void {
    this._started = true;
  }

  initTasks(loadedTasks: LoadedTaskInput[]): void {
    if (this._initialized) return;
    this.tasks.push(...this.staticTasks.map(task => new TaskRun(task.id, task.title, task.uses, task.with)));
    this.tasks.push(...loadedTasks.map(task => new TaskRun(task.id, task.title, task.uses, task.with)));
    this.checks.push(...this.staticChecks.map(check => new StageCheck(check.name, check.title, check.uses, check.with)));
    this._initialized = true;
  }

  nextWork(): StageWork {
    if (this.status === 'passed') return { kind: 'complete' };
    if (this.status === 'awaiting-approval') return { kind: 'await-approval' };
    if (this.status === 'failed') return { kind: 'blocked', reason: 'stage-failed' };
    if (this.status !== 'running') return { kind: 'blocked', reason: 'stage-not-running' };

    if (!this._initialized) {
      const source = typeof this.tasksFrom === 'string'
        ? { uses: this.tasksFrom }
        : this.tasksFrom;
      return {
        kind: 'stage-init',
        definition: {
          tasksFrom: source ? { uses: source.uses, with: source.with } : undefined,
        },
      };
    }

    const task = this.currentTask;
    if (task) {
      return {
        kind: 'task',
        task: { id: task.id, title: task.title, uses: task.uses, with: task.withInput },
      };
    }

    const check = this.pendingCheck;
    if (check) {
      return {
        kind: 'check',
        check: { name: check.name, title: check.title, uses: check.uses, with: check.withInput },
      };
    }

    if (this.requiresApproval && !this.approval) {
      return { kind: 'await-approval' };
    }

    return { kind: 'complete' };
  }

  completeTask(): void {
    const task = this.currentTask;
    if (!task) return;
    task.start();
    task.complete();
  }

  failTask(reason?: string): void {
    const task = this.currentTask;
    if (!task) return;
    task.start();
    task.fail();
    this.failure = {
      reason: 'task-failed',
      stage: this.stage,
      taskId: task.id,
      message: reason,
    };
  }

  passCheck(result: { message?: string; output?: unknown }): void {
    const check = this.requirePendingCheck();
    check.message = result.message ?? null;
    check.output = result.output ?? null;
    check.pass();
  }

  resetCheck(result: { message?: string; output?: unknown }): void {
    const check = this.requirePendingCheck();
    check.message = result.message ?? null;
    check.output = result.output ?? null;
    check.reset();
  }

  failCheck(result: { message?: string; output?: unknown }): void {
    const check = this.requirePendingCheck();
    check.message = result.message ?? null;
    check.output = result.output ?? null;
    check.fail();
    this.failure = {
      reason: 'check-unrepaired',
      stage: this.stage,
      checkName: check.name,
      message: result.message,
    };
  }

  requestApproval(output?: unknown): void {
    this.approval = {
      status: 'awaiting',
      output: output ?? null,
      requestedAt: new Date().toISOString(),
      respondedAt: null,
    };
  }

  approve(input: ApprovalInput = {}): void {
    if (this.approval?.status !== 'awaiting') {
      throw new WorkflowDomainError(`Stage ${this.stage} is not awaiting approval`);
    }
    this.approval = {
      status: 'approved',
      output: input.output ?? null,
      requestedAt: this.approval.requestedAt,
      respondedAt: new Date().toISOString(),
    };
  }

  reject(input: ApprovalInput = {}): void {
    if (this.approval?.status !== 'awaiting') {
      throw new WorkflowDomainError(`Stage ${this.stage} is not awaiting approval`);
    }
    this.failure = {
      reason: 'approval-rejected',
      stage: this.stage,
      message: typeof input.output === 'string' ? input.output : undefined,
    };
    this.approval = {
      status: 'rejected',
      output: input.output ?? null,
      requestedAt: this.approval.requestedAt,
      respondedAt: new Date().toISOString(),
    };
  }

  private get pendingCheck(): StageCheck | null {
    return this.checks.find(candidate => candidate.status === 'pending') ?? null;
  }

  private requirePendingCheck(): StageCheck {
    const check = this.pendingCheck;
    if (!check) throw new WorkflowDomainError(`No pending check in stage ${this.stage}`);
    return check;
  }
}
