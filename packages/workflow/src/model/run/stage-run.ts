import type { StageDefinition, TaskDefinition, WorkflowStageId } from '../workflow-definition';
import { StageCheck } from './stage-check';
import { TaskRun } from './task-run';
import { type ApprovalState, type CommitPoint, type FailureDetails, type StageRunStatus, type WorkSourceState } from './types';

export class StageRun {
  readonly tasks: TaskRun[];
  readonly checks: StageCheck[];
  status: StageRunStatus = 'pending';
  attemptSequence = 1;
  approval: ApprovalState | null = null;
  failure: FailureDetails | null = null;
  commitPoint: CommitPoint | null = null;
  workSourceState: WorkSourceState = { evaluated: false };

  constructor(
    readonly definition: StageDefinition,
    readonly order: number,
  ) {
    this.tasks = definition.tasks.map(() => new TaskRun());
    this.checks = definition.checks.map(check => new StageCheck(check.name, check.title));
  }

  get stage(): WorkflowStageId {
    return this.definition.stage;
  }

  start(): void {
    this.status = 'running';
  }

  get currentTask(): TaskRun | null {
    return this.tasks.find(task => task.status !== 'completed' && task.status !== 'failed') ?? null;
  }

  get currentTaskDefinition(): TaskDefinition | null {
    const task = this.currentTask;
    if (!task) return null;
    return this.definition.tasks[this.tasks.indexOf(task)] ?? null;
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

  addTask(id: string, title: string, uses?: string): TaskRun {
    this.definition.tasks.push({ id, title, uses });
    const task = new TaskRun();
    this.tasks.push(task);
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
