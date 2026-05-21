import { WorkflowRun } from '../model';
import type { WorkflowComponentRegistry } from './component-registry';

type TaskWork = Extract<ReturnType<WorkflowRun['next']>, { kind: 'task' }>;

export class TaskRunner {
  constructor(private readonly registry: WorkflowComponentRegistry) {}

  async run(workflowRun: WorkflowRun, work: TaskWork): Promise<boolean> {
    const handler = this.registry.task(work.task.uses);
    if (!handler) return false;
    const result = await handler.run({
      id: work.task.id,
      title: work.task.title,
      with: work.task.with ? { ...work.task.with } : undefined,
    });
    if (result.status === 'completed') {
      workflowRun.completeTask();
      return true;
    }
    workflowRun.failTask(result);
    return false;
  }
}
