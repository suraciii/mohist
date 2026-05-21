import type { StageDefinition, WorkflowStageId } from '../workflow-definition';
import { StageCheck } from './stage-check';
import { TaskRun } from './task-run';
import { type ApprovalState, type FailureDetails, type MaterializedTaskInput, type StageRunStatus } from './types';

export class StageRun {
  readonly tasks: TaskRun[];
  readonly checks: StageCheck[];
  status: StageRunStatus = 'pending';
  approval: ApprovalState | null = null;
  failure: FailureDetails | null = null;
  initialized = false;

  constructor(
    readonly definition: StageDefinition,
    readonly order: number,
  ) {
    this.tasks = [];
    this.checks = [];
  }

  get stage(): WorkflowStageId {
    return this.definition.stage;
  }

  start(): void {
    this.status = 'running';
  }

  initTasks(materializedTasks: MaterializedTaskInput[]): void {
    if (this.initialized) return;
    this.tasks.push(...this.definition.tasks.map(task => new TaskRun(task.id, task.title, task.uses, task.with)));
    this.tasks.push(...materializedTasks.map(task => new TaskRun(task.id, task.title, task.uses, task.with)));
    this.checks.push(...this.definition.checks.map(check => new StageCheck(check.name, check.title, check.uses, check.with)));
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
