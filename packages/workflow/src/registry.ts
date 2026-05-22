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

export interface HandlerRegistry {
  task(uses: string | undefined): TaskHandler | null;
  check(uses: string | undefined): CheckHandler | null;
  taskSource(uses: string | undefined): TaskSourceHandler | null;
}

export class Registry implements HandlerRegistry {
  private readonly tasks = new Map<string, TaskHandler>();
  private readonly checks = new Map<string, CheckHandler>();
  private readonly taskSources = new Map<string, TaskSourceHandler>();

  registerTask(uses: string, handler: TaskHandler): void {
    this.tasks.set(uses, handler);
  }

  registerCheck(uses: string, handler: CheckHandler): void {
    this.checks.set(uses, handler);
  }

  registerTaskSource(uses: string, handler: TaskSourceHandler): void {
    this.taskSources.set(uses, handler);
  }

  task(uses: string | undefined): TaskHandler | null {
    if (!uses) return null;
    return this.tasks.get(uses) ?? null;
  }

  check(uses: string | undefined): CheckHandler | null {
    if (!uses) return null;
    return this.checks.get(uses) ?? null;
  }

  taskSource(uses: string | undefined): TaskSourceHandler | null {
    if (!uses) return null;
    return this.taskSources.get(uses) ?? null;
  }
}
