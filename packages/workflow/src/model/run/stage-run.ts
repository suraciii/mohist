import type { WorkflowStageId, WorkflowTasksFromSource } from '../workflow-definition';
import { StageCheck } from './stage-check';
import { TaskRun } from './task-run';
import type { ApprovalState, FailureDetails, MaterializedTaskInput, StageRunStatus } from './types';

export class StageRun {
  readonly tasks: TaskRun[];
  readonly checks: StageCheck[];
  status: StageRunStatus = 'pending';
  approval: ApprovalState | null = null;
  failure: FailureDetails | null = null;
  initialized = false;

  constructor(
    readonly stage: WorkflowStageId,
    readonly order: number,
    private readonly staticTasks: { id: string; title: string; uses?: string; with?: Record<string, unknown> }[],
    private readonly staticChecks: { name: string; title: string; uses?: string; with?: Record<string, unknown> }[],
    readonly tasksFrom?: WorkflowTasksFromSource,
    readonly requiresApproval?: boolean,
    readonly approvalCheckName?: string,
  ) {
    this.tasks = [];
    this.checks = [];
  }

  start(): void {
    this.status = 'running';
  }

  initTasks(materializedTasks: MaterializedTaskInput[]): void {
    if (this.initialized) return;
    this.tasks.push(...this.staticTasks.map(task => new TaskRun(task.id, task.title, task.uses, task.with)));
    this.tasks.push(...materializedTasks.map(task => new TaskRun(task.id, task.title, task.uses, task.with)));
    this.checks.push(...this.staticChecks.map(check => new StageCheck(check.name, check.title, check.uses, check.with)));
    this.initialized = true;
  }

  get currentTask(): TaskRun | null {
    return this.tasks.find(task => task.status !== 'completed' && task.status !== 'failed') ?? null;
  }

  startTask(): TaskRun | null {
    const task = this.currentTask;
    if (!task) return null;
    task.start();
    return task;
  }

  completeTask(): TaskRun | null {
    const task = this.currentTask;
    if (!task) return null;
    task.complete();
    return task;
  }

  failTask(): TaskRun | null {
    const task = this.currentTask;
    if (!task) return null;
    task.fail();
    return task;
  }

  passCheck(name: string): StageCheck | null {
    const check = this.checks.find(candidate => candidate.name === name);
    if (!check) return null;
    check.pass();
    return check;
  }

  failCheck(name: string): StageCheck | null {
    const check = this.checks.find(candidate => candidate.name === name);
    if (!check) return null;
    check.fail();
    return check;
  }

  resetCheck(name: string): StageCheck | null {
    const check = this.checks.find(candidate => candidate.name === name);
    if (!check) return null;
    check.reset();
    return check;
  }
}
