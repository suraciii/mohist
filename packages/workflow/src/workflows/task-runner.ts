import { WorkflowRun } from '../model';
import type { WorkflowComponentRegistry } from './component-registry';
import type { WorkflowTaskResult } from './types';

type TaskWork = Extract<ReturnType<WorkflowRun['next']>, { kind: 'task' }>;

export class TaskRunner {
  constructor(private readonly registry: WorkflowComponentRegistry) {}

  async run(work: TaskWork): Promise<WorkflowTaskResult | null> {
    const handler = this.registry.task(work.task.uses);
    if (!handler) return null;
    return handler.run({
      id: work.task.id,
      title: work.task.title,
      with: work.task.with ? { ...work.task.with } : undefined,
    });
  }
}
