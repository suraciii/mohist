import { WorkflowRun } from './model';
import { WorkflowComponentRegistry } from './component-registry';
import { workflowDefinitionFromInput } from './workflow-definition-input';
import { WorkflowRunner } from './workflow-runner';
import type { CreateWorkflowsInput, Workflows, WorkflowCreateInput } from './workflow-types';

export * from './workflow-types';

export function createWorkflows(input: CreateWorkflowsInput): Workflows {
  const registry = new WorkflowComponentRegistry();
  for (const component of input.components ?? []) {
    registry.register(component);
  }

  return {
    async create(createInput: WorkflowCreateInput) {
      const definition = workflowDefinitionFromInput(createInput.definition);
      const run = new WorkflowRun(createInput.id, definition.stages);
      return new WorkflowRunner(run, input.store, registry);
    },

    async load(id) {
      const run = await input.store.load(id);
      if (!run) return null;
      return new WorkflowRunner(run, input.store, registry);
    },

    register(component) {
      registry.register(component);
    },
  };
}
