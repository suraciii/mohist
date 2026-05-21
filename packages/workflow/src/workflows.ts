import { WorkflowRun } from './model';
import { WorkflowComponentRegistry } from './workflows/component-registry';
import { resolvedWorkflowDefinitionFromInput } from './workflows/definition-input';
import { WorkflowRunner } from './workflows/workflow-runner';
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
      const run = new WorkflowRun(createInput.id, {
        workflowDefinitionId: definition.resolvedDefinition.id,
        stages: definition.resolvedDefinition.stages,
      });
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
