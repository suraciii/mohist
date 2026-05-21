import type { CompiledStageDefinition, WorkflowStageId } from '../workflow-definition';
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
    readonly definition: CompiledStageDefinition,
    readonly order: number,
  ) {
    this.tasks = definition.tasks.map(task => new TaskRun(task.id, task.title, task.uses));
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

  startTask(): TaskRun | null {
    const task = this.currentTask;
    if (!task) return null;
    task.start();
    return task;
  }

  completeTask(result: { output?: unknown; events?: string[]; reason?: string } = {}): TaskRun | null {
    const task = this.currentTask;
    if (!task) return null;
    task.complete(result);
    return task;
  }

  failTask(result: { output?: unknown; events?: string[]; reason?: string } = {}): TaskRun | null {
    const task = this.currentTask;
    if (!task) return null;
    task.fail(result);
    return task;
  }

  addTask(id: string, title: string, uses?: string): TaskRun {
    const task = new TaskRun(id, title, uses);
    this.tasks.push(task);
    return task;
  }

  addCheck(name: string, title: string): StageCheck {
    const check = new StageCheck(name, title);
    this.checks.push(check);
    return check;
  }
}
