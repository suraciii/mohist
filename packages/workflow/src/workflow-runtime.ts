import { WorkflowRun } from './model';
import { workflowDefinitionFromInput } from './workflow-definition-input';
import { WorkflowRunner } from './workflow-runner';
import type { CreateWorkflowRuntimeInput, WorkflowCreateInput, WorkflowRuntime } from './workflow-types';

export * from './workflow-types';

export function createWorkflowRuntime(input: CreateWorkflowRuntimeInput): WorkflowRuntime {
  return {
    async create(createInput: WorkflowCreateInput) {
      const definition = workflowDefinitionFromInput(createInput.definition);
      const run = new WorkflowRun(createInput.id, definition.stages);
      return new WorkflowRunner(run, input.store, input.registry);
    },

    async load(id) {
      const run = await input.store.load(id);
      if (!run) return null;
      return new WorkflowRunner(run, input.store, input.registry);
    },
  };
}

export { Registry } from './registry';
