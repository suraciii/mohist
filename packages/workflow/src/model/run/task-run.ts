import type { TaskRunStatus } from './types';

export class TaskRun {
  status: TaskRunStatus = 'pending';

  resetForFreshAttempt(): void {
    this.status = 'pending';
  }

  start(): void {
    this.status = 'running';
  }

  complete(): void {
    this.status = 'completed';
  }

  fail(): void {
    this.status = 'failed';
  }
}
