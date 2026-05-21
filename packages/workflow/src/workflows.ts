import { WorkflowRun } from './model';
import { Registry } from './registry';
import { workflowDefinitionFromInput } from './workflow-definition-input';
import { WorkflowRunner } from './workflow-runner';
import type { CreateWorkflowsInput, Workflows, WorkflowCreateInput } from './workflow-types';

export * from './workflow-types';

export function createWorkflows(input: CreateWorkflowsInput): Workflows {
  const registry = new Registry();

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
  };
}

export { Registry } from './registry';
