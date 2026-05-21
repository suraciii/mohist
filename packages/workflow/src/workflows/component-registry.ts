import type {
  WorkflowCheckType,
  WorkflowComponent,
  WorkflowTaskSourceType,
  WorkflowTaskType,
} from './types';

export class WorkflowComponentRegistry {
  private readonly tasks = new Map<string, WorkflowTaskType>();
  private readonly checks = new Map<string, WorkflowCheckType>();
  private readonly taskSources = new Map<string, WorkflowTaskSourceType>();

  register(component: WorkflowComponent): void {
    if (component.type === 'task') {
      this.tasks.set(component.uses, component);
      return;
    }
    if (component.type === 'check') {
      this.checks.set(component.uses, component);
      return;
    }
    this.taskSources.set(component.uses, component);
  }

  task(uses: string | undefined): WorkflowTaskType | null {
    if (!uses) return null;
    return this.tasks.get(uses) ?? null;
  }

  check(uses: string | undefined): WorkflowCheckType | null {
    if (!uses) return null;
    return this.checks.get(uses) ?? null;
  }

  taskSource(uses: string | undefined): WorkflowTaskSourceType | null {
    if (!uses) return null;
    return this.taskSources.get(uses) ?? null;
  }
}
