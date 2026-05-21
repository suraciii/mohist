import type { TaskRunStatus } from './types';

export class TaskRun {
  status: TaskRunStatus = 'pending';
  events: string[] = [];
  output: unknown | null = null;
  reason: string | null = null;

  constructor(
    readonly id: string,
    readonly title: string,
    readonly uses?: string,
  ) {}

  resetForFreshAttempt(): void {
    this.status = 'pending';
    this.events = [];
    this.output = null;
    this.reason = null;
  }

  start(): void {
    this.status = 'running';
  }

  complete(result: { output?: unknown; events?: string[]; reason?: string } = {}): void {
    this.status = 'completed';
    this.output = result.output ?? this.output;
    this.events = result.events ?? this.events;
    this.reason = result.reason ?? this.reason;
  }

  fail(result: { output?: unknown; events?: string[]; reason?: string } = {}): void {
    this.status = 'failed';
    this.output = result.output ?? this.output;
    this.events = result.events ?? this.events;
    this.reason = result.reason ?? this.reason;
  }
}
