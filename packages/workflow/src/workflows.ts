import { WorkflowRun } from './model';
import { WorkflowComponentRegistry } from './workflows/component-registry';
import { workflowDefinitionSnapshotFromInput } from './workflows/definition-input';
import { RunnableWorkflow } from './workflows/runnable-workflow';
import { workflowRunFromState } from './workflows/workflow-state';
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
      const definition = workflowDefinitionSnapshotFromInput(createInput.definition, createInput.now);
      const { run } = WorkflowRun.startWorkflow({
        id: createInput.id,
        issueId: createInput.id,
        issueNumber: 0,
        workflowDefinitionSnapshot: definition,
        now: createInput.now,
      });
      return new RunnableWorkflow(run, input.store, registry, input.maxSteps);
    },

    async load(id) {
      const state = await input.store.load(id);
      if (!state) return null;
      return new RunnableWorkflow(workflowRunFromState(state), input.store, registry, input.maxSteps);
    },

    register(component) {
      registry.register(component);
    },
  };
}
