import type { TaskLoadResult, WorkflowTaskInput, WorkflowCheckInput } from './workflow-types';

export interface TaskHandler {
  run(input: WorkflowTaskInput): Promise<{ status: 'completed' | 'failed'; reason?: string }>;
}

export interface CheckHandler {
  run(input: WorkflowCheckInput): Promise<{ status: 'pass' | 'fail' | 'error' | 'pending'; message?: string; output?: unknown }>;
}

export interface TaskLoader {
  load(input: {
    run: unknown;
    stage: string;
    definition: { uses: string; with?: Record<string, unknown> };
  }): Promise<TaskLoadResult>;
}

export interface HandlerRegistry {
  task(uses: string | undefined): TaskHandler | null;
  check(uses: string | undefined): CheckHandler | null;
  taskLoader(uses: string | undefined): TaskLoader | null;
}

export class Registry implements HandlerRegistry {
  private readonly tasks = new Map<string, TaskHandler>();
  private readonly checks = new Map<string, CheckHandler>();
  private readonly taskLoaders = new Map<string, TaskLoader>();

  registerTask(uses: string, handler: TaskHandler): void {
    this.tasks.set(uses, handler);
  }

  registerCheck(uses: string, handler: CheckHandler): void {
    this.checks.set(uses, handler);
  }

  registerTaskLoader(uses: string, loader: TaskLoader): void {
    this.taskLoaders.set(uses, loader);
  }

  task(uses: string | undefined): TaskHandler | null {
    if (!uses) return null;
    return this.tasks.get(uses) ?? null;
  }

  check(uses: string | undefined): CheckHandler | null {
    if (!uses) return null;
    return this.checks.get(uses) ?? null;
  }

  taskLoader(uses: string | undefined): TaskLoader | null {
    if (!uses) return null;
    return this.taskLoaders.get(uses) ?? null;
  }
}
