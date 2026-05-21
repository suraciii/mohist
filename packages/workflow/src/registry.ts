import type { Check } from './checks';
import type { WorkflowTaskSourceResult, WorkflowTaskInput, WorkflowCheckInput } from './workflow-types';

export interface TaskHandler {
  run(input: WorkflowTaskInput): Promise<{ status: 'completed' | 'failed'; reason?: string }>;
}

export interface CheckHandler {
  run(input: WorkflowCheckInput): Promise<{ status: 'pass' | 'fail' | 'error' | 'pending'; message?: string; output?: unknown }>;
}

export interface TaskSourceHandler {
  create(context: { run: unknown }): {
    createTasks(input: {
      run: unknown;
      stage: string;
      definition: { uses: string; with?: Record<string, unknown> };
    }): Promise<WorkflowTaskSourceResult>;
  };
}

export class Registry {
  private readonly tasks = new Map<string, TaskHandler>();
  private readonly checks = new Map<string, CheckHandler>();
  private readonly taskSources = new Map<string, TaskSourceHandler>();
  private readonly checkProviders = new Map<string, { build: (input: unknown) => Promise<Check | null> }>();
  private readonly markerFormats = new Map<string, unknown>();

  task(uses: string | undefined): TaskHandler | null {
    if (!uses) return null;
    return this.tasks.get(uses) ?? null;
  }

  registerTask(uses: string, handler: TaskHandler): void {
    this.tasks.set(uses, handler);
  }

  check(uses: string | undefined): CheckHandler | null {
    if (!uses) return null;
    return this.checks.get(uses) ?? null;
  }

  registerCheck(uses: string, handler: CheckHandler): void {
    this.checks.set(uses, handler);
  }

  taskSource(uses: string | undefined): TaskSourceHandler | null {
    if (!uses) return null;
    return this.taskSources.get(uses) ?? null;
  }

  registerTaskSource(uses: string, handler: TaskSourceHandler): void {
    this.taskSources.set(uses, handler);
  }

  registerCheckProvider(id: string, provider: { build: (input: unknown) => Promise<Check | null> }): void {
    this.checkProviders.set(id, provider);
  }

  registerMarkerFormat(format: string, handler: unknown): void {
    this.markerFormats.set(format, handler);
  }

  getMarkerFormat(format: string | undefined): unknown {
    return format ? this.markerFormats.get(format) : undefined;
  }
}
