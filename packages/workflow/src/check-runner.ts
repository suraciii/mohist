import { WorkflowRun } from './model';
import type { WorkflowComponentRegistry } from './component-registry';
import type { WorkflowCheckResult } from './workflow-types';

type CheckWork = Extract<ReturnType<WorkflowRun['next']>, { kind: 'check' }>;

export class CheckRunner {
  constructor(private readonly registry: WorkflowComponentRegistry) {}

  async run(work: CheckWork): Promise<WorkflowCheckResult | null> {
    const handler = this.registry.check(work.check.uses);
    if (!handler) return null;
    return handler.run({
      name: work.check.name,
      title: work.check.title,
      with: work.check.with ? { ...work.check.with } : undefined,
    });
  }
}
