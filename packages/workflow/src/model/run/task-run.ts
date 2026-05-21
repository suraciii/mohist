import type { TaskRunStatus } from './types';

export class TaskRun {
  status: TaskRunStatus = 'pending';

  constructor(
    readonly id: string,
    readonly title: string,
    readonly uses?: string,
    readonly withInput?: Record<string, unknown>,
  ) {}

  reset(): void {
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
