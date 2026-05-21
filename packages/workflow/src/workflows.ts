import { WorkflowRun } from './model';
import { WorkflowComponentRegistry } from './workflows/component-registry';
import { resolvedWorkflowDefinitionFromInput } from './workflows/definition-input';
import { RunnableWorkflow } from './workflows/runnable-workflow';
import { runFromRecord } from './workflows/workflow-run-record';
import type {
  CreateWorkflowsInput,
  Workflows,
} from './workflows/types';

export * from './workflows/types';

export function createWorkflows(input: CreateWorkflowsInput): Workflows {
  const registry = new WorkflowComponentRegistry();
  for (const component of input.components ?? []) {
    registry.register(component);
  }

  return {
    async create(createInput) {
      const definition = resolvedWorkflowDefinitionFromInput(createInput.definition, createInput.now);
      const { run } = WorkflowRun.startWorkflow({
        id: createInput.id,
        issueId: createInput.id,
        issueNumber: 0,
        definition: definition,
        now: createInput.now,
      });
      return new RunnableWorkflow(run, input.store, registry, input.maxSteps);
    },

    async load(id) {
      const record = await input.store.load(id);
      if (!record) return null;
      return new RunnableWorkflow(runFromRecord(record), input.store, registry, input.maxSteps);
    },

    register(component) {
      registry.register(component);
    },
  };
}
